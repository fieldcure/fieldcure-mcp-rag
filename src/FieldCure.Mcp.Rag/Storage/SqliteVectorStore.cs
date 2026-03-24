using System.Numerics;
using System.Runtime.InteropServices;
using FieldCure.Mcp.Rag.Models;
using Microsoft.Data.Sqlite;

namespace FieldCure.Mcp.Rag.Storage;

/// <summary>
/// SQLite-backed store for document chunks and their embedding vectors.
/// Provides cosine similarity search via full-scan with SIMD acceleration.
/// </summary>
public sealed class SqliteVectorStore : IDisposable
{
    readonly string _connectionString;

    public SqliteVectorStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        InitializeSchema();
    }

    void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS chunks (
                id           TEXT PRIMARY KEY,
                source_path  TEXT NOT NULL,
                chunk_index  INTEGER NOT NULL,
                content      TEXT NOT NULL,
                char_offset  INTEGER NOT NULL DEFAULT 0,
                metadata     TEXT NOT NULL DEFAULT '{}'
            );

            CREATE TABLE IF NOT EXISTS embeddings (
                chunk_id     TEXT PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
                model        TEXT NOT NULL,
                embedding    BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS file_index (
                source_path  TEXT PRIMARY KEY,
                file_hash    TEXT NOT NULL,
                indexed_at   TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_chunks_source ON chunks(source_path);
            """;
        cmd.ExecuteNonQuery();
    }

    SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Upserts a chunk and its embedding vector.
    /// Uses INSERT OR REPLACE semantics keyed on chunk.Id.
    /// </summary>
    public async Task UpsertChunkAsync(DocumentChunk chunk, float[] embedding, string modelId)
    {
        await using var conn = OpenConnection();

        await using var chunkCmd = conn.CreateCommand();
        chunkCmd.CommandText = """
            INSERT OR REPLACE INTO chunks (id, source_path, chunk_index, content, char_offset, metadata)
            VALUES (@id, @source_path, @chunk_index, @content, @char_offset, @metadata)
            """;
        chunkCmd.Parameters.AddWithValue("@id", chunk.Id);
        chunkCmd.Parameters.AddWithValue("@source_path", chunk.SourcePath);
        chunkCmd.Parameters.AddWithValue("@chunk_index", chunk.ChunkIndex);
        chunkCmd.Parameters.AddWithValue("@content", chunk.Content);
        chunkCmd.Parameters.AddWithValue("@char_offset", chunk.CharOffset);
        chunkCmd.Parameters.AddWithValue("@metadata", chunk.Metadata);
        await chunkCmd.ExecuteNonQueryAsync();

        await using var embCmd = conn.CreateCommand();
        embCmd.CommandText = """
            INSERT OR REPLACE INTO embeddings (chunk_id, model, embedding)
            VALUES (@chunk_id, @model, @embedding)
            """;
        embCmd.Parameters.AddWithValue("@chunk_id", chunk.Id);
        embCmd.Parameters.AddWithValue("@model", modelId);
        embCmd.Parameters.AddWithValue("@embedding", SerializeVector(embedding));
        await embCmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes all chunks and embeddings for the given source file path.
    /// Called before re-indexing a modified file.
    /// </summary>
    public async Task DeleteBySourcePathAsync(string sourcePath)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chunks WHERE source_path = @source_path";
        cmd.Parameters.AddWithValue("@source_path", sourcePath);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Returns the SHA256 hash stored for a file path, or null if not indexed.
    /// </summary>
    public async Task<string?> GetFileHashAsync(string sourcePath)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_hash FROM file_index WHERE source_path = @source_path";
        cmd.Parameters.AddWithValue("@source_path", sourcePath);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    /// <summary>Upserts the file hash record after successful indexing.</summary>
    public async Task SetFileHashAsync(string sourcePath, string hash)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO file_index (source_path, file_hash, indexed_at)
            VALUES (@source_path, @file_hash, @indexed_at)
            """;
        cmd.Parameters.AddWithValue("@source_path", sourcePath);
        cmd.Parameters.AddWithValue("@file_hash", hash);
        cmd.Parameters.AddWithValue("@indexed_at", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Performs cosine similarity search over all stored embeddings.
    /// Uses SIMD-accelerated dot product via System.Numerics.Vector.
    /// Returns top-k results with score >= threshold.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(
        float[] queryEmbedding,
        int topK = 5,
        float threshold = 0.5f)
    {
        var candidates = new List<(string ChunkId, float Score)>();

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT chunk_id, embedding FROM embeddings";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var chunkId = reader.GetString(0);
            var blob = (byte[])reader[1];
            var stored = DeserializeVector(blob);

            var score = CosineSimilarity(queryEmbedding, stored);
            if (score >= threshold)
                candidates.Add((chunkId, score));
        }

        var topResults = candidates
            .OrderByDescending(c => c.Score)
            .Take(topK)
            .ToList();

        var results = new List<SearchResult>();
        foreach (var (chunkId, score) in topResults)
        {
            var chunk = await GetChunkAsync(chunkId);
            if (chunk is not null)
            {
                results.Add(new SearchResult
                {
                    ChunkId = chunk.Id,
                    SourcePath = chunk.SourcePath,
                    ChunkIndex = chunk.ChunkIndex,
                    Content = chunk.Content,
                    Score = score,
                });
            }
        }

        return results;
    }

    /// <summary>Retrieves a single chunk by its ID.</summary>
    public async Task<DocumentChunk?> GetChunkAsync(string chunkId)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, source_path, chunk_index, content, char_offset, metadata
            FROM chunks WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", chunkId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new DocumentChunk
        {
            Id = reader.GetString(0),
            SourcePath = reader.GetString(1),
            ChunkIndex = reader.GetInt32(2),
            Content = reader.GetString(3),
            CharOffset = reader.GetInt32(4),
            Metadata = reader.GetString(5),
        };
    }

    /// <summary>Returns all indexed source file paths.</summary>
    public async Task<List<string>> GetIndexedPathsAsync()
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_path FROM file_index";

        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            paths.Add(reader.GetString(0));
        return paths;
    }

    /// <summary>
    /// Removes all chunks, embeddings, and the file_index record for the given path.
    /// Called when a previously indexed file no longer exists on disk.
    /// </summary>
    public async Task PurgeSourcePathAsync(string sourcePath)
    {
        await DeleteBySourcePathAsync(sourcePath);

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM file_index WHERE source_path = @source_path";
        cmd.Parameters.AddWithValue("@source_path", sourcePath);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Returns total number of chunks stored.</summary>
    public async Task<int> GetTotalChunkCountAsync()
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public void Dispose()
    {
        // Connection pooling is handled by SqliteConnection; nothing to dispose at store level.
    }

    static byte[] SerializeVector(float[] vector)
    {
        return MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
    }

    static float[] DeserializeVector(byte[] bytes)
    {
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0f;

        var dot = 0f;
        var normA = 0f;
        var normB = 0f;

        var simdLength = Vector<float>.Count;
        var i = 0;

        var vDot = Vector<float>.Zero;
        var vNormA = Vector<float>.Zero;
        var vNormB = Vector<float>.Zero;

        for (; i <= a.Length - simdLength; i += simdLength)
        {
            var va = new Vector<float>(a, i);
            var vb = new Vector<float>(b, i);
            vDot += va * vb;
            vNormA += va * va;
            vNormB += vb * vb;
        }

        dot = Vector.Dot(vDot, Vector<float>.One);
        normA = Vector.Dot(vNormA, Vector<float>.One);
        normB = Vector.Dot(vNormB, Vector<float>.One);

        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0f ? 0f : dot / denominator;
    }
}
