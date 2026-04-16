using System.Diagnostics;
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
    /// <summary>
    /// Target schema version for this code release. Bumped whenever a breaking
    /// schema change is introduced. Independent of release version (v1.4.x).
    /// Written to <c>PRAGMA user_version</c> at the end of <see cref="InitializeSchema"/>.
    /// Databases created before v1.4.1 report 0 (SQLite default), regardless
    /// of which actual schema columns they contain.
    /// </summary>
    public const int TargetUserVersion = 2;

    readonly string _connectionString;

    public SqliteVectorStore(string dbPath, bool readOnly = false)
    {
        if (!readOnly)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        if (!readOnly)
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
                enriched     TEXT,
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

            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                chunk_id,
                content,
                tokenize = 'trigram'
            );

            CREATE TABLE IF NOT EXISTS index_metadata (
                key   TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE TABLE IF NOT EXISTS _indexing_lock (
                id       INTEGER PRIMARY KEY CHECK (id = 1),
                pid      INTEGER NOT NULL,
                started  TEXT NOT NULL,
                current  INTEGER NOT NULL DEFAULT 0,
                total    INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrations
        MigrateEnrichedColumn(conn);
        MigrateV04StatusColumns(conn);

        // Tag the database with the current schema version. This is the only
        // place user_version is written. Safe because InitializeSchema() runs
        // only when readOnly == false (constructor guard).
        using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = $"PRAGMA user_version = {TargetUserVersion};";
        versionCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the schema version stored in this database's <c>PRAGMA user_version</c>
    /// header field. Cheap (microseconds) — reads from page 0, which is already in
    /// memory after connection open. Never triggers migration.
    /// Prefer this method when the caller already has a store instance; use
    /// <see cref="ReadUserVersion"/> otherwise.
    /// </summary>
    /// <returns>
    /// Current user_version value. Returns 0 for legacy databases that were
    /// never tagged (all KBs created before v1.4.1).
    /// </returns>
    public int GetUserVersion()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result ?? 0);
    }

    /// <summary>
    /// Static helper for callers that do not already have a store instance
    /// (diagnostic tools, tests). Opens the database read-only, reads
    /// <c>PRAGMA user_version</c>, and disposes. Always opens read-only —
    /// never triggers migration.
    /// </summary>
    /// <param name="dbPath">Absolute path to the KB's SQLite file.</param>
    /// <returns>
    /// Current user_version value. Returns 0 for legacy databases.
    /// </returns>
    public static int ReadUserVersion(string dbPath)
    {
        using var store = new SqliteVectorStore(dbPath, readOnly: true);
        return store.GetUserVersion();
    }

    #region Schema Migrations

    /// <summary>
    /// Adds a column to a table if it does not already exist.
    /// Uses <c>PRAGMA table_info</c> to check for the column name.
    /// </summary>
    static bool AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        reader.Close();

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
        return true;
    }

    /// <summary>
    /// v1.1.0 → v1.2.0: adds the 'enriched' column to the chunks table.
    /// </summary>
    static void MigrateEnrichedColumn(SqliteConnection conn)
    {
        if (AddColumnIfMissing(conn, "chunks", "enriched", "TEXT"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE chunks SET enriched = content WHERE enriched IS NULL";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// v1.3.x → v1.4.0: adds status tracking columns to chunks, file_index, and _indexing_lock.
    /// </summary>
    static void MigrateV04StatusColumns(SqliteConnection conn)
    {
        // chunks: per-chunk status tracking
        AddColumnIfMissing(conn, "chunks", "status", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "chunks", "is_contextualized", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(conn, "chunks", "last_error", "TEXT");
        AddColumnIfMissing(conn, "chunks", "retry_count", "INTEGER NOT NULL DEFAULT 0");

        // file_index: per-file status tracking
        AddColumnIfMissing(conn, "file_index", "status", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "file_index", "chunks_raw", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "file_index", "chunks_contextualized", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "file_index", "chunks_pending", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "file_index", "last_error", "TEXT");
        AddColumnIfMissing(conn, "file_index", "last_error_stage", "TEXT");

        // _indexing_lock: per-run status tracking
        AddColumnIfMissing(conn, "_indexing_lock", "current_stage", "TEXT");
        AddColumnIfMissing(conn, "_indexing_lock", "failed_count", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "_indexing_lock", "provider_health", "INTEGER NOT NULL DEFAULT 0");

        // Partial index for pending/failed chunk queries
        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_chunks_status ON chunks(status) WHERE status != 0";
        idxCmd.ExecuteNonQuery();
    }

    #endregion

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
    public async Task UpsertChunkAsync(DocumentChunk chunk, float[] embedding, string modelId, string? enrichedText = null)
    {
        var info = enrichedText is not null
            ? new ChunkWriteInfo { EnrichedText = enrichedText, Status = Models.ChunkIndexStatus.Indexed, IsContextualized = true }
            : null;
        await using var conn = OpenConnection();
        await UpsertChunkAsync(conn, null, chunk, embedding, modelId, info);
    }

    /// <summary>
    /// Inserts (or replaces) a chunk row, optionally its embedding row, and its FTS row.
    /// When <paramref name="embedding"/> is <c>null</c>, the embeddings table is left
    /// untouched — used by <see cref="PersistChunksAsPendingAsync"/> for the
    /// 2-commit model where chunks are persisted before embedding.
    /// </summary>
    async Task UpsertChunkAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        DocumentChunk chunk, float[]? embedding, string modelId,
        ChunkWriteInfo? info = null)
    {
        var enriched = info?.EnrichedText ?? chunk.Content;
        var status = (int)(info?.Status ?? Models.ChunkIndexStatus.Indexed);
        var isContextualized = info?.IsContextualized ?? true;
        var lastError = info?.LastError;

        await using var chunkCmd = conn.CreateCommand();
        chunkCmd.Transaction = tx;
        chunkCmd.CommandText = """
            INSERT OR REPLACE INTO chunks
                (id, source_path, chunk_index, content, enriched, char_offset, metadata,
                 status, is_contextualized, last_error, retry_count)
            VALUES
                (@id, @source_path, @chunk_index, @content, @enriched, @char_offset, @metadata,
                 @status, @is_contextualized, @last_error, 0)
            """;
        chunkCmd.Parameters.AddWithValue("@id", chunk.Id);
        chunkCmd.Parameters.AddWithValue("@source_path", chunk.SourcePath);
        chunkCmd.Parameters.AddWithValue("@chunk_index", chunk.ChunkIndex);
        chunkCmd.Parameters.AddWithValue("@content", chunk.Content);
        chunkCmd.Parameters.AddWithValue("@enriched", enriched);
        chunkCmd.Parameters.AddWithValue("@char_offset", chunk.CharOffset);
        chunkCmd.Parameters.AddWithValue("@metadata", chunk.Metadata);
        chunkCmd.Parameters.AddWithValue("@status", status);
        chunkCmd.Parameters.AddWithValue("@is_contextualized", isContextualized ? 1 : 0);
        chunkCmd.Parameters.AddWithValue("@last_error", (object?)lastError ?? DBNull.Value);
        await chunkCmd.ExecuteNonQueryAsync();

        if (embedding is not null)
        {
            await using var embCmd = conn.CreateCommand();
            embCmd.Transaction = tx;
            embCmd.CommandText = """
                INSERT OR REPLACE INTO embeddings (chunk_id, model, embedding)
                VALUES (@chunk_id, @model, @embedding)
                """;
            embCmd.Parameters.AddWithValue("@chunk_id", chunk.Id);
            embCmd.Parameters.AddWithValue("@model", modelId);
            embCmd.Parameters.AddWithValue("@embedding", SerializeVector(embedding));
            await embCmd.ExecuteNonQueryAsync();
        }

        // FTS5 sync: delete-then-insert (virtual tables don't support REPLACE)
        // Index enriched text for improved search. We insert the FTS row even for
        // PendingEmbedding chunks so that BM25 keyword search works on them
        // immediately — vector search will skip them naturally because no
        // embeddings row exists yet.
        await using var ftsDelCmd = conn.CreateCommand();
        ftsDelCmd.Transaction = tx;
        ftsDelCmd.CommandText = "DELETE FROM chunks_fts WHERE chunk_id = @id";
        ftsDelCmd.Parameters.AddWithValue("@id", chunk.Id);
        await ftsDelCmd.ExecuteNonQueryAsync();

        await using var ftsInsCmd = conn.CreateCommand();
        ftsInsCmd.Transaction = tx;
        ftsInsCmd.CommandText = "INSERT INTO chunks_fts (chunk_id, content) VALUES (@id, @content)";
        ftsInsCmd.Parameters.AddWithValue("@id", chunk.Id);
        ftsInsCmd.Parameters.AddWithValue("@content", enriched);
        await ftsInsCmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes all chunks and embeddings for the given source file path.
    /// Called before re-indexing a modified file.
    /// </summary>
    public async Task DeleteBySourcePathAsync(string sourcePath)
    {
        await using var conn = OpenConnection();
        await DeleteBySourcePathAsync(conn, null, sourcePath);
    }

    async Task DeleteBySourcePathAsync(SqliteConnection conn, SqliteTransaction? tx, string sourcePath)
    {
        // FTS5 cleanup must run before chunks deletion (needs chunks table for subquery)
        await using var ftsCmd = conn.CreateCommand();
        ftsCmd.Transaction = tx;
        ftsCmd.CommandText = """
            DELETE FROM chunks_fts WHERE chunk_id IN
                (SELECT id FROM chunks WHERE source_path = @source_path)
            """;
        ftsCmd.Parameters.AddWithValue("@source_path", sourcePath);
        await ftsCmd.ExecuteNonQueryAsync();

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM chunks WHERE source_path = @source_path";
        cmd.Parameters.AddWithValue("@source_path", sourcePath);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Atomically replaces all chunks for a source file within a single transaction.
    /// Includes file_index upsert when <paramref name="fileInfo"/> is provided.
    /// If any step fails, the old data is preserved (rollback).
    /// </summary>
    public async Task ReplaceFileChunksAsync(
        string sourcePath,
        IReadOnlyList<DocumentChunk> chunks,
        float[][] embeddings,
        string modelId,
        IReadOnlyList<ChunkWriteInfo>? chunkInfo = null,
        FileWriteInfo? fileInfo = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(embeddings);

        if (embeddings.Length != chunks.Count)
            throw new ArgumentException(
                $"embeddings.Length ({embeddings.Length}) must equal chunks.Count ({chunks.Count}).");

        if (chunkInfo is not null && chunkInfo.Count != chunks.Count)
            throw new ArgumentException(
                $"chunkInfo.Count ({chunkInfo.Count}) must equal chunks.Count ({chunks.Count}).");

        if (chunks.Count == 0)
            return; // Nothing to replace; preserve existing data.

        await using var conn = OpenConnection();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            await DeleteBySourcePathAsync(conn, (SqliteTransaction)tx, sourcePath);

            for (var i = 0; i < chunks.Count; i++)
            {
                await UpsertChunkAsync(
                    conn, (SqliteTransaction)tx,
                    chunks[i], embeddings[i], modelId, chunkInfo?[i]);
            }

            if (fileInfo is not null)
                await UpsertFileIndexAsync(conn, (SqliteTransaction)tx, sourcePath, fileInfo);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Commit 1 of the v1.4.2 2-commit model: persists chunks and file_index BEFORE
    /// embedding, so that expensive upstream results (OCR, chunking, contextualization)
    /// survive any failure during the embed stage. Atomic — DELETE old, INSERT new
    /// chunks, INSERT FTS rows, UPSERT file_index — all in a single transaction.
    /// </summary>
    /// <param name="sourcePath">Storage path of the file being indexed.</param>
    /// <param name="chunks">Chunks to persist. Each chunk's <see cref="DocumentChunk.Id"/> must be set.</param>
    /// <param name="chunkInfos">
    /// Per-chunk write info. Caller is expected to set
    /// <see cref="ChunkWriteInfo.Status"/> to <see cref="Models.ChunkIndexStatus.PendingEmbedding"/>
    /// (or another non-Indexed status) and provide the contextualized
    /// <see cref="ChunkWriteInfo.EnrichedText"/> that the second pass will eventually
    /// embed without re-running upstream stages.
    /// </param>
    /// <param name="fileInfo">
    /// File-level info. Caller is expected to set
    /// <see cref="FileWriteInfo.Status"/> to <see cref="Models.FileIndexStatus.PartiallyDeferred"/>
    /// and <see cref="FileWriteInfo.ChunksPending"/> to the chunk count.
    /// </param>
    /// <param name="ct">Cancellation token. Cancellation is checked before transaction begin only.</param>
    public async Task PersistChunksAsPendingAsync(
        string sourcePath,
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<ChunkWriteInfo> chunkInfos,
        FileWriteInfo fileInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(chunkInfos);
        ArgumentNullException.ThrowIfNull(fileInfo);

        if (chunkInfos.Count != chunks.Count)
            throw new ArgumentException(
                $"chunkInfos.Count ({chunkInfos.Count}) must equal chunks.Count ({chunks.Count}).");

        if (chunks.Count == 0)
            return; // Nothing to persist; preserve existing data.

        ct.ThrowIfCancellationRequested();

        await using var conn = OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await DeleteBySourcePathAsync(conn, (SqliteTransaction)tx, sourcePath);

            for (var i = 0; i < chunks.Count; i++)
            {
                // embedding: null → UpsertChunkAsync skips the embeddings table insert
                // but still writes the chunk row and the FTS row.
                await UpsertChunkAsync(
                    conn, (SqliteTransaction)tx,
                    chunks[i], embedding: null, modelId: string.Empty, chunkInfos[i]);
            }

            await UpsertFileIndexAsync(conn, (SqliteTransaction)tx, sourcePath, fileInfo);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Commit 2a of the v1.4.2 2-commit model: promotes PendingEmbedding chunks to
    /// Indexed and inserts their embeddings in a single transaction. Called after
    /// successful <c>EmbedBatchAsync</c> on chunks previously persisted via
    /// <see cref="PersistChunksAsPendingAsync"/>.
    /// </summary>
    /// <param name="sourcePath">Storage path of the file being promoted.</param>
    /// <param name="chunkIds">
    /// Chunk identifiers to promote. Order must match <paramref name="embeddings"/>.
    /// All listed chunks should currently be in <see cref="Models.ChunkIndexStatus.PendingEmbedding"/>;
    /// the UPDATE silently ignores chunks that no longer exist (e.g., if the file
    /// was re-indexed concurrently — this is acceptable, the rollback path will
    /// rebuild correctly).
    /// </param>
    /// <param name="embeddings">Embedding vectors, one per chunk id.</param>
    /// <param name="modelId">Embedding model identifier stored in the embeddings table.</param>
    /// <param name="fileStatus">
    /// Final file status. Use <see cref="Models.FileIndexStatus.Ready"/> for a clean
    /// run, or <see cref="Models.FileIndexStatus.Degraded"/> when some chunks lacked
    /// contextualization but were still embedded successfully.
    /// </param>
    /// <param name="chunksPending">
    /// Remaining pending chunk count (0 for full success, &gt;0 for partial promotion
    /// — relevant once batch splitting lands in v1.4.3).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PromoteChunksToIndexedAsync(
        string sourcePath,
        IReadOnlyList<string> chunkIds,
        float[][] embeddings,
        string modelId,
        Models.FileIndexStatus fileStatus = Models.FileIndexStatus.Ready,
        int chunksPending = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(chunkIds);
        ArgumentNullException.ThrowIfNull(embeddings);

        if (embeddings.Length != chunkIds.Count)
            throw new ArgumentException(
                $"embeddings.Length ({embeddings.Length}) must equal chunkIds.Count ({chunkIds.Count}).");

        if (chunkIds.Count == 0)
            return;

        ct.ThrowIfCancellationRequested();

        await using var conn = OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // Promote each chunk to Indexed and insert its embedding row. Both
            // statements share parameter slots that are updated per iteration to
            // avoid repeated command construction.
            await using var promoteCmd = conn.CreateCommand();
            promoteCmd.Transaction = (SqliteTransaction)tx;
            promoteCmd.CommandText = """
                UPDATE chunks
                SET status = @status,
                    retry_count = 0,
                    last_error = NULL
                WHERE id = @id
                """;
            var pStatus = promoteCmd.Parameters.Add("@status", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pId = promoteCmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
            pStatus.Value = (int)Models.ChunkIndexStatus.Indexed;

            await using var embCmd = conn.CreateCommand();
            embCmd.Transaction = (SqliteTransaction)tx;
            embCmd.CommandText = """
                INSERT OR REPLACE INTO embeddings (chunk_id, model, embedding)
                VALUES (@chunk_id, @model, @embedding)
                """;
            var pChunkId = embCmd.Parameters.Add("@chunk_id", Microsoft.Data.Sqlite.SqliteType.Text);
            var pModel = embCmd.Parameters.Add("@model", Microsoft.Data.Sqlite.SqliteType.Text);
            var pEmbedding = embCmd.Parameters.Add("@embedding", Microsoft.Data.Sqlite.SqliteType.Blob);
            pModel.Value = modelId;

            for (var i = 0; i < chunkIds.Count; i++)
            {
                pId.Value = chunkIds[i];
                await promoteCmd.ExecuteNonQueryAsync(ct);

                pChunkId.Value = chunkIds[i];
                pEmbedding.Value = SerializeVector(embeddings[i]);
                await embCmd.ExecuteNonQueryAsync(ct);
            }

            // Update file_index status atomically with the chunk promotions.
            await using var fileCmd = conn.CreateCommand();
            fileCmd.Transaction = (SqliteTransaction)tx;
            fileCmd.CommandText = """
                UPDATE file_index
                SET status = @status,
                    chunks_pending = @chunks_pending,
                    indexed_at = @indexed_at,
                    last_error = NULL,
                    last_error_stage = NULL
                WHERE source_path = @source_path
                """;
            fileCmd.Parameters.AddWithValue("@status", (int)fileStatus);
            fileCmd.Parameters.AddWithValue("@chunks_pending", chunksPending);
            fileCmd.Parameters.AddWithValue("@indexed_at", DateTime.UtcNow.ToString("O"));
            fileCmd.Parameters.AddWithValue("@source_path", sourcePath);
            await fileCmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Upserts file_index row within an existing transaction.
    /// </summary>
    async Task UpsertFileIndexAsync(
        SqliteConnection conn, SqliteTransaction tx, string sourcePath, FileWriteInfo info)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO file_index
                (source_path, file_hash, indexed_at, status, chunks_raw, chunks_contextualized, chunks_pending, last_error, last_error_stage)
            VALUES
                (@source_path, @file_hash, @indexed_at, @status, @chunks_raw, @chunks_contextualized, @chunks_pending, @last_error, @last_error_stage)
            ON CONFLICT(source_path) DO UPDATE SET
                file_hash = excluded.file_hash,
                indexed_at = excluded.indexed_at,
                status = excluded.status,
                chunks_raw = excluded.chunks_raw,
                chunks_contextualized = excluded.chunks_contextualized,
                chunks_pending = excluded.chunks_pending,
                last_error = excluded.last_error,
                last_error_stage = excluded.last_error_stage
            """;
        cmd.Parameters.AddWithValue("@source_path", sourcePath);
        cmd.Parameters.AddWithValue("@file_hash", info.FileHash);
        cmd.Parameters.AddWithValue("@indexed_at", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@status", (int)info.Status);
        cmd.Parameters.AddWithValue("@chunks_raw", info.ChunksRaw);
        cmd.Parameters.AddWithValue("@chunks_contextualized", info.ChunksContextualized);
        cmd.Parameters.AddWithValue("@chunks_pending", info.ChunksPending);
        cmd.Parameters.AddWithValue("@last_error", (object?)info.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_error_stage", (object?)info.LastErrorStage ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Marks a file as failed without writing or deleting any chunks.
    /// Preserves any previously indexed chunks — only updates file_index status.
    /// For files that were never indexed (no file_index row), this is a no-op and
    /// the method returns <c>false</c> so the caller can detect and react — e.g.
    /// log a warning that the file will surface as "added" in check_changes on
    /// the next run.
    /// </summary>
    /// <returns>
    /// <c>true</c> if an existing file_index row was updated; <c>false</c> if no
    /// row matched the supplied <paramref name="sourcePath"/>.
    /// </returns>
    public async Task<bool> MarkFileAsFailedAsync(
        string sourcePath,
        FileIndexStatus status,
        string errorMessage,
        string errorStage)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE file_index
            SET status = @status, last_error = @last_error, last_error_stage = @last_error_stage
            WHERE source_path = @source_path
            """;
        cmd.Parameters.AddWithValue("@source_path", sourcePath);
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@last_error", errorMessage);
        cmd.Parameters.AddWithValue("@last_error_stage", errorStage);
        var rowsAffected = await cmd.ExecuteNonQueryAsync();
        return rowsAffected > 0;
    }

    /// <summary>
    /// Returns the number of file_index rows currently in the given status.
    /// Used by <c>IndexingEngine</c> to sanity-check its in-memory counters
    /// against the actual DB state at the end of a run (e.g., to detect
    /// a drift between "partiallyDeferred" the variable and rows with
    /// <see cref="Models.FileIndexStatus.PartiallyDeferred"/>).
    /// </summary>
    /// <summary>
    /// Returns a snapshot of the current file_index state — not a delta from
    /// any particular run. Used for end-of-run summary and get_index_info.
    /// </summary>
    public async Task<IndexStateCounters> GetStateCountersAsync()
    {
        await using var conn = OpenConnection();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    COALESCE(SUM(CASE WHEN status = @failed THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN status = @degraded THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN status = @deferred THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN status = @needsAction THEN 1 ELSE 0 END), 0)
                FROM file_index
                """;
            cmd.Parameters.AddWithValue("@failed", (int)FileIndexStatus.Failed);
            cmd.Parameters.AddWithValue("@degraded", (int)FileIndexStatus.Degraded);
            cmd.Parameters.AddWithValue("@deferred", (int)FileIndexStatus.PartiallyDeferred);
            cmd.Parameters.AddWithValue("@needsAction", (int)FileIndexStatus.NeedsAction);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return new IndexStateCounters(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }
        catch (SqliteException) { }
        return new IndexStateCounters(0, 0, 0, 0);
    }

    public async Task<int> CountFilesByStatusAsync(FileIndexStatus status)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM file_index WHERE status = @status";
        cmd.Parameters.AddWithValue("@status", (int)status);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Returns chunks in <see cref="Models.ChunkIndexStatus.PendingEmbedding"/> state,
    /// ordered by retry count (lowest first) for deferred retry.
    /// </summary>
    public async Task<IReadOnlyList<PendingChunk>> GetPendingEmbeddingChunksAsync(int maxCount = 100)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, source_path, chunk_index, content, enriched, retry_count
            FROM chunks
            WHERE status = @status
            ORDER BY retry_count ASC, source_path
            LIMIT @maxCount
            """;
        cmd.Parameters.AddWithValue("@status", (int)Models.ChunkIndexStatus.PendingEmbedding);
        cmd.Parameters.AddWithValue("@maxCount", maxCount);

        var results = new List<PendingChunk>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new PendingChunk(
                Id: reader.GetString(0),
                SourcePath: reader.GetString(1),
                ChunkIndex: reader.GetInt32(2),
                Content: reader.GetString(3),
                EnrichedText: reader.IsDBNull(4) ? reader.GetString(3) : reader.GetString(4),
                RetryCount: reader.GetInt32(5)));
        }

        return results;
    }

    /// <summary>
    /// Updates a single chunk's status, typically after a deferred embedding retry.
    /// Always increments retry_count. If <paramref name="embedding"/> is provided
    /// and <paramref name="newStatus"/> is <see cref="Models.ChunkIndexStatus.Indexed"/>,
    /// the embedding row is upserted within the same transaction.
    /// </summary>
    public async Task UpdateChunkStatusAsync(
        string chunkId,
        Models.ChunkIndexStatus newStatus,
        string modelId,
        float[]? embedding = null,
        string? lastError = null)
    {
        await using var conn = OpenConnection();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                UPDATE chunks
                SET status = @status,
                    retry_count = retry_count + 1,
                    last_error = @last_error
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@id", chunkId);
            cmd.Parameters.AddWithValue("@status", (int)newStatus);
            cmd.Parameters.AddWithValue("@last_error", (object?)lastError ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();

            if (embedding is not null && newStatus == Models.ChunkIndexStatus.Indexed)
            {
                await using var embCmd = conn.CreateCommand();
                embCmd.Transaction = (SqliteTransaction)tx;
                embCmd.CommandText = """
                    INSERT OR REPLACE INTO embeddings (chunk_id, model, embedding)
                    VALUES (@chunk_id, @model, @embedding)
                    """;
                embCmd.Parameters.AddWithValue("@chunk_id", chunkId);
                embCmd.Parameters.AddWithValue("@model", modelId);
                embCmd.Parameters.AddWithValue("@embedding", SerializeVector(embedding));
                await embCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Returns the SHA256 hash stored for a file path, or null if not indexed.
    /// Prefer <see cref="GetFileStateAsync"/> when the caller needs to
    /// distinguish "file unchanged and fully indexed" from "file unchanged
    /// but previously deferred" — this method loses that distinction.
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

    /// <summary>
    /// Returns the stored hash and status of a file in file_index, or null if
    /// there is no row for the given path. Used by <c>IndexingEngine</c>'s
    /// hash-skip logic to decide whether a file needs re-extraction
    /// (Ready/Degraded with same hash → skip) or should be left alone for the
    /// deferred retry pass (PartiallyDeferred with same hash → also skip, but
    /// for a different reason: Commit 1 already persisted the upstream work).
    /// </summary>
    public async Task<FileState?> GetFileStateAsync(string sourcePath)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_hash, status FROM file_index WHERE source_path = @source_path";
        cmd.Parameters.AddWithValue("@source_path", sourcePath);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return new FileState(reader.GetString(0), (FileIndexStatus)reader.GetInt32(1));
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

    /// <summary>Returns the most recent indexed_at timestamp (ISO 8601 UTC), or null if empty.</summary>
    public async Task<string?> GetLastIndexedAtAsync()
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(indexed_at) FROM file_index";
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
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

    /// <summary>
    /// Returns aggregate contextualization stats across all files.
    /// </summary>
    public async Task<(int TotalContextualized, int TotalRaw, int FilesDegraded)> GetContextualizationStatsAsync()
    {
        await using var conn = OpenConnection();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    COALESCE(SUM(chunks_contextualized), 0),
                    COALESCE(SUM(chunks_raw), 0),
                    COALESCE(SUM(CASE WHEN chunks_raw > 0 THEN 1 ELSE 0 END), 0)
                FROM file_index
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Legacy DB without chunks_contextualized/chunks_raw columns
        }
        return (0, 0, 0);
    }

    /// <summary>
    /// Performs BM25 full-text search via FTS5 with trigram tokenizer.
    /// Filters out query tokens shorter than 3 characters (trigram minimum).
    /// Returns empty list if no valid tokens remain (caller should fall back to vector search).
    /// </summary>
    public async Task<List<(string ChunkId, double Score)>> SearchFtsAsync(string query, int topK)
    {
        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrEmpty(ftsQuery))
            return [];

        var results = new List<(string ChunkId, double Score)>();

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT chunk_id, bm25(chunks_fts) as score
            FROM chunks_fts
            WHERE chunks_fts MATCH @query
            ORDER BY bm25(chunks_fts)
            LIMIT @topK
            """;
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@topK", topK);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var chunkId = reader.GetString(0);
                var score = reader.GetDouble(1);
                // bm25() returns negative values (lower = more relevant); negate for consistency
                results.Add((chunkId, -score));
            }
        }
        catch (SqliteException)
        {
            // Malformed query or FTS5 error — return empty results gracefully
            return [];
        }

        return results;
    }

    /// <summary>
    /// Retrieves multiple chunks by their IDs in a single query.
    /// Includes total chunk count per source path for has_previous/has_next computation.
    /// </summary>
    public async Task<List<DocumentChunk>> GetChunksByIdsAsync(IReadOnlyList<string> chunkIds)
    {
        if (chunkIds.Count == 0)
            return [];

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();

        var paramNames = new string[chunkIds.Count];
        for (int i = 0; i < chunkIds.Count; i++)
        {
            paramNames[i] = $"@p{i}";
            cmd.Parameters.AddWithValue(paramNames[i], chunkIds[i]);
        }

        var inClause = string.Join(", ", paramNames);
        cmd.CommandText = $"""
            SELECT c.id, c.source_path, c.chunk_index, c.content, c.char_offset, c.metadata,
                   (SELECT COUNT(*) FROM chunks c2 WHERE c2.source_path = c.source_path) as total_chunks
            FROM chunks c
            WHERE c.id IN ({inClause})
            """;

        var results = new List<DocumentChunk>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DocumentChunk
            {
                Id = reader.GetString(0),
                SourcePath = reader.GetString(1),
                ChunkIndex = reader.GetInt32(2),
                Content = reader.GetString(3),
                CharOffset = reader.GetInt32(4),
                Metadata = reader.GetString(5),
                TotalChunks = reader.GetInt32(6),
            });
        }

        return results;
    }

    /// <summary>
    /// Builds an FTS5 MATCH query from user input.
    /// Drops tokens shorter than 3 characters (trigram tokenizer minimum).
    /// Joins remaining tokens with OR for broad matching.
    /// </summary>
    internal static string BuildFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";

        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .Select(EscapeFtsToken)
            .ToList();

        return tokens.Count == 0 ? "" : string.Join(" OR ", tokens);
    }

    /// <summary>
    /// Wraps an FTS5 token in double quotes, escaping internal quotes.
    /// </summary>
    static string EscapeFtsToken(string token)
    {
        // Wrap in double quotes to handle special characters in FTS5
        return $"\"{token.Replace("\"", "\"\"")}\"";
    }

    #region Metadata

    /// <summary>Gets a metadata value by key, or null if not found.</summary>
    public async Task<string?> GetMetadataAsync(string key)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM index_metadata WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    /// <summary>Sets a metadata key-value pair (upsert).</summary>
    public async Task SetMetadataAsync(string key, string? value)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();

        if (value is null)
        {
            cmd.CommandText = "DELETE FROM index_metadata WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
        }
        else
        {
            cmd.CommandText = """
                INSERT OR REPLACE INTO index_metadata (key, value)
                VALUES (@key, @value)
                """;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Gets all metadata as a dictionary.</summary>
    public async Task<Dictionary<string, string>> GetAllMetadataAsync()
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM index_metadata";

        var result = new Dictionary<string, string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    #endregion

    #region Indexing Lock

    /// <summary>Lock info returned by <see cref="GetLockInfo"/>.</summary>
    public record IndexingLockInfo(bool IsIndexing, int Current, int Total, int Pid);

    /// <summary>
    /// Attempts to acquire the indexing lock for the given process.
    /// Returns true if acquired, false if another live process holds it.
    /// Stale locks (dead processes) are automatically cleaned up.
    /// </summary>
    public bool AcquireLock(int pid)
    {
        using var conn = OpenConnection();
        CleanStaleLock(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO _indexing_lock (id, pid, started, current, total)
            VALUES (1, @pid, @started, 0, 0)
            """;
        cmd.Parameters.AddWithValue("@pid", pid);
        cmd.Parameters.AddWithValue("@started", DateTime.UtcNow.ToString("O"));
        var rows = cmd.ExecuteNonQuery();
        return rows > 0;
    }

    /// <summary>
    /// Updates the indexing progress in the lock row.
    /// Null parameters preserve existing values via COALESCE.
    /// </summary>
    public void UpdateProgress(
        int current,
        int total,
        string? currentStage = null,
        int? failedCount = null,
        Models.ProviderHealth? providerHealth = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE _indexing_lock SET
                current = @current,
                total = @total,
                current_stage = COALESCE(@current_stage, current_stage),
                failed_count = COALESCE(@failed_count, failed_count),
                provider_health = COALESCE(@provider_health, provider_health)
            WHERE id = 1
            """;
        cmd.Parameters.AddWithValue("@current", current);
        cmd.Parameters.AddWithValue("@total", total);
        cmd.Parameters.AddWithValue("@current_stage", (object?)currentStage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@failed_count", (object?)failedCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@provider_health", providerHealth.HasValue ? (int)providerHealth.Value : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Releases the indexing lock by deleting the row.</summary>
    public void ReleaseLock()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM _indexing_lock WHERE id = 1";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the current lock status. Cleans up stale locks automatically.
    /// </summary>
    public IndexingLockInfo GetLockInfo()
    {
        using var conn = OpenConnection();
        CleanStaleLock(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pid, current, total FROM _indexing_lock WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new IndexingLockInfo(false, 0, 0, 0);

        return new IndexingLockInfo(
            true,
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(0));
    }

    /// <summary>Checks if the lock holder process is still alive; deletes the row if not.</summary>
    static void CleanStaleLock(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pid FROM _indexing_lock WHERE id = 1";
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull)
            return;

        var pid = Convert.ToInt32(result);
        if (!IsProcessAlive(pid))
        {
            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM _indexing_lock WHERE id = 1";
            delCmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Checks whether a process with the given PID is still running.
    /// </summary>
    static bool IsProcessAlive(int pid)
    {
        var procs = Process.GetProcesses();
        try { return procs.Any(p => p.Id == pid); }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    #endregion

    public void Dispose()
    {
        // Connection pooling is handled by SqliteConnection; nothing to dispose at store level.
    }

    /// <summary>Serializes a float vector to a byte array for SQLite BLOB storage.</summary>
    static byte[] SerializeVector(float[] vector)
    {
        return MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
    }

    /// <summary>Deserializes a byte array from SQLite BLOB back to a float vector.</summary>
    static float[] DeserializeVector(byte[] bytes)
    {
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    /// <summary>Computes cosine similarity between two vectors using SIMD acceleration.</summary>
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
