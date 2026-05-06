# Release Notes

## v2.4.4 (2026-05-06)

### Fixed

- **First-launch RAG connect race against `prune-orphans` on clean
  PCs.** The host (AssistStudio) previously spawned two `dnx`
  invocations of `FieldCure.Mcp.Rag` back-to-back at startup —
  `RagProcessManager.StartPruneOrphans()` for the orphan cleanup,
  and `ConnectBuiltInAsync(rag)` for the MCP serve connection —
  and on a cold cache the two contended for the same nuget
  fetch lock. One side consistently dropped out after roughly
  1.6 s with `server shut down unexpectedly`, and the user saw
  RAG fail at first launch even though every subsequent attempt
  succeeded. `RunServeAsync` now calls
  `OrphanCleanupRunner.RunAsync` itself, right before
  `app.RunAsync()`. There is one `dnx` invocation per serve
  start, the race window is gone, and the host no longer needs
  to schedule a separate prune step. `emitJson` is set to
  `false` for this in-process call because stdout is owned by
  the MCP wire protocol once the host is built; the prune
  result is reported through the logger only. Prune failures
  are caught and logged so a prune problem cannot prevent
  serve from starting.

### Changed

- **`OrphanCleanupRunner.RunAsync` gains a 5 s mtime grace.**
  A freshly created KB folder may briefly exist on disk
  without `config.json` while the App writes it, and prune
  must not delete that folder. Folders whose last-write time
  falls inside the grace window are skipped on this pass; a
  later prune (or the next serve startup) will clean them up
  if they really are orphans. Tests pass `mtimeGrace =
  TimeSpan.Zero` to opt out of the protection where they need
  deterministic deletion.
- **`OrphanCleanupRunner.RunAsync` gains an `emitJson`
  parameter (default `true`).** The CLI `prune-orphans` mode
  keeps its existing JSON-to-stdout contract; only the
  serve-startup caller passes `false`. The CLI's JSON schema
  is byte-compatible with v2.4.3 — no keys added, no keys
  removed.

### Tests

- Nine new cases in `OrphanCleanupRunnerTests` covering live-KB
  preservation, aged orphan deletion, the young-orphan grace,
  the `grace = TimeSpan.Zero` escape hatch, non-GUID and
  prefix/backup-protected folders, both `emitJson` contracts
  (CLI writes to stdout, serve stays silent), and the
  missing-base-path exit code.

### Migration

- Hosts that currently spawn `prune-orphans` separately (notably
  AssistStudio's `RagProcessManager.StartPruneOrphans()`) can
  remove that call once they upgrade to the v2.4.4 RAG tool.
  Doing so removes the cold-fetch race entirely. Hosts that do
  not remove the call still work — the prune step simply runs
  twice on launch (no correctness impact, just a redundant
  process spawn).

## v2.4.3 (2026-05-06)

### Fixed

- **Stuck `start_reindex` after the orchestrator was killed mid-run.**
  `start_reindex` consults a queue-file flag (`StartedAt` on the entry)
  while `get_index_info` consults a SQLite row, and `IndexingEngine`'s
  `finally` only clears the SQLite side. When the orchestrator child
  process was killed before its own per-entry `try/finally` could run
  — OS kill, OOM, segfault, the parent serve being terminated, the
  user closing the host app while a long whisper transcription was
  active — the queue mark survived on disk while the in-process
  finalizer never ran. Every subsequent `start_reindex` then bounced
  on `"already_running"` while `get_index_info` simultaneously reported
  the KB as idle, and the user could see "ready" in the UI yet not
  start anything until the queue file was hand-edited or the data
  directory wiped. v2.4.3 introduces a stale-lock recovery sweep that
  detects this state and re-queues the entry through the normal
  failure-replace branch:
  - A new `IsOrchestratorAlive(basePath)` helper reads
    `orchestrator.lock`, validates the recorded PID is alive, and
    keeps the existing PID-reuse / start-time defense.
  - A new `RecoverStaleRunningEntries(queueFilePath, logger)` sweep
    converts every `StartedAt`-set entry into the failed state with
    `LastError = "orchestrator_died_or_killed"`.
  - `ExecQueueRunner.RunAsync` calls the sweep unconditionally right
    after acquiring its own orchestrator lock — any `StartedAt` it
    inherits at that point is necessarily from a previous orchestrator
    that did not finish cleanly.
  - `StartReindexTool` and `CancelReindexTool` call the sweep
    conditionally on entry, only when no live orchestrator owns the
    lock, so users can recover from the chat surface without
    restarting anything.
  - `force=true` is left untouched: bypassing the lock guard while a
    real orchestrator is running risks corrupting in-flight SQLite
    writes, and the recovery path already handles every dead-
    orchestrator case automatically.

- **`IsLockStale` PID-reuse defense was timezone-broken.**
  The function compared `process.StartTime.ToUniversalTime()` against
  `DateTime.TryParse(lockInfo.StartedAt, ...)`, which on a Z-suffixed
  ISO string returns `Kind = Local` (the value is converted into local
  time despite the trailing `Z`). On any non-UTC host the subtraction
  produced a difference equal to the local offset — for example
  ~32400 seconds in KST — and the 5-second skew tolerance always
  flagged a perfectly valid live lock as "stale." The new tests for
  the recovery flow surfaced this latent bug. Parsing now uses
  `AdjustToUniversal | AssumeUniversal`, keeping both sides in UTC.

### Tests

- Ten new cases covering the recovery primitives, `IsLockStale` TZ
  behavior, and the `start_reindex` / `cancel_reindex` tool surface
  end-to-end. Validated outside the test suite by killing a live
  indexing orchestrator mid-whisper-transcription (~4.5 GB RAM in
  use) and confirming the next `start_reindex` auto-recovered to
  `status = "queued"` with no manual intervention.

## v2.4.2 (2026-04-28)

### Fixed

- **Cached read store opened with `SqliteOpenMode.ReadOnly` broke under
  WAL mode.** `MultiKbContext.GetKb` caches a logically-read-only
  `SqliteVectorStore` per knowledge base for the MCP serve path
  (`get_index_info`, `search_documents`, `list_knowledge_bases`, …).
  The store was opened with `Mode = SqliteOpenMode.ReadOnly`, which
  maps to `O_RDONLY` at the OS level. WAL mode, however, requires
  every connection — readers included — to register itself in the
  `-shm` shared-memory file, which is a filesystem write. With an
  `O_RDONLY` file handle that registration fails the moment any read
  query touches a WAL-mode database, surfacing as
  `SqliteException: SQLite Error 8: 'attempt to write a readonly
  database'` (the message is misleading — the rejected write is
  SQLite's own `-shm` coordination, not a query). The cached store
  stayed broken until the serve process restarted, so every
  subsequent `get_index_info` poll on that KB returned the same
  error. v2.4.2 opens read-only stores with `SqliteOpenMode.ReadWrite`
  instead so the OS handle has write permission for `-shm`
  registration, while the existing `if (!readOnly) InitializeSchema()`
  guard keeps the read path from issuing any schema mutation.

- **Orchestrator spawn dropped its arguments when launched via `dotnet
  tool` / `dotnet dnx`.** `start_reindex` writes the entry to
  `.deferred-queue.json` and then calls `TrySpawnOrchestrator`, which
  used `Environment.ProcessPath` plus the literal `"exec-queue
  --queue-file ..."` arguments. When the server runs through `dotnet
  dnx FieldCure.Mcp.Rag@2.* --yes serve` (the default integration for
  `dotnet tool` / MCP host installs), `Environment.ProcessPath`
  resolves to `dotnet.exe`, not the tool dll, so the spawned command
  was `dotnet.exe exec-queue --queue-file ...` — which fails
  immediately because `dotnet`'s CLI has no `exec-queue` subcommand.
  Process.Start did not throw (the failed child exited cleanly), the
  warning catch block stayed silent, and the queue file accumulated
  entries with nothing to drain them. Symptom: clicking **변경 확인**
  or any reindex action looked like it queued the request, but
  `is_indexing` never flipped and the lock file never appeared.
  v2.4.2 detects when the host is `dotnet`/`dotnet.exe` and prepends
  the dll path resolved from `typeof(StartReindexTool).Assembly.Location`
  before the `exec-queue` arguments, so the orchestrator gets the
  same launch shape as the manual `dotnet <dll> exec-queue ...`
  invocation that was always working.

- **Brand-new KB tool failures returned unparseable plain text.** When a
  tool threw on a freshly-created knowledge base — typically the very
  first `check_changes` or `get_index_info` call against a KB with only
  `config.json` and no `rag.db` yet — the MCP framework's default error
  path wrapped the exception in a plain-text reply
  (`"An error occurred invoking 'check_changes'."`). Programmatic
  clients (e.g., `AssistStudio.KbMcpClient`) tried to `JsonDocument.Parse`
  that text and failed with `JsonReaderException: 'A' is an invalid
  start of a value`. v2.3.1 fixed the pattern in `get_index_info` only;
  v2.4.2 extends the same structured-error wrapper to the four tools
  that still bubbled exceptions:
  - `check_changes`
  - `cancel_reindex`
  - `get_document_chunk`
  - `list_knowledge_bases`

### API contract — structured error envelope

Failed tool invocations now return a JSON envelope:

```json
{
  "kb_id": "…",
  "status": "error",
  "error": "FileNotFoundException: Database not found for knowledge base: …"
}
```

- **LLM-based MCP hosts** (Claude Desktop, Anthropic CLI, etc.) need
  no changes. The JSON renders naturally as tool output and the model
  sees a more specific failure reason than the previous plain-text
  reply.
- **Programmatic hosts** that deserialize tool responses should check
  `status == "error"` before accessing data fields. AssistStudio
  v0.18+ ships this guard in `KbMcpClient.GetIndexInfoAsync` /
  `CheckChangesAsync`; other consumers parsing these tools directly
  should apply the same pattern.

### Why this surfaced now

`get_index_info`'s wrapper has been in place since v2.3.1, but the
other four tools went untouched because every previous test case
exercised KBs that already had a `rag.db` from at least one successful
indexing run. The first reproduction came from creating a KB and
hitting **변경 확인** (check changes) before any indexing had ever
written `rag.db` — a common path for new installs.

### Audit completed alongside the fix

A full grep of `Console.WriteLine` / `Console.Out` / `Console.Write`
across `src/` confirmed the MCP stdio transport stays clean: the
`[Audio]` startup banner is on `Console.Error`, ASP.NET logging is
routed to stderr through `LogToStandardErrorThreshold = Trace`, and
the only `Console.Out` write outside `serve` mode is the JSON output
of the `prune-orphans` CLI command.

### Migration

- `dotnet tool update fieldcure-mcp-rag` (or the equivalent dnx
  refresh through your MCP host) picks up the patch automatically.
- No schema change. No data migration. No breaking change for
  consumers that did not parse error responses.

## v2.4.1 (2026-04-28)

### Fixed

- **Multi-channel WAV transcription.** v2.4.0 shipped with
  `FieldCure.DocumentParsers.Audio` v0.3.0 frozen into the tool nupkg.
  v0.3.0's `AudioConverter` rejected `WAVE_FORMAT_EXTENSIBLE` PCM input
  (Windows capture pipelines, modern DAW exports, any WAV with more than
  two channels) at `MediaFoundationResampler` construction with
  `ArgumentException("Input must be PCM or IEEE float ...")`. v2.4.1
  republishes against `Audio` v0.3.1, which detects the extensible format
  tag and re-labels the `WaveFormat` as standard PCM / IEEE float without
  byte conversion. Standard-PCM WAVs and MP3 / OGG / M4A / FLAC / WebM
  inputs are unaffected.

### Why this is a republish, not just a transitive bump

`FieldCure.Mcp.Rag` ships as a `dotnet tool` (`PackAsTool=true`), which
bundles the exact `FieldCure.DocumentParsers.Audio.dll` resolved at pack
time inside its own .nupkg. v2.4.0's `Audio="0.3.*"` PackageReference pin
only takes effect at maintainer-side pack time; existing v2.4.0
installations carry the v0.3.0 `Audio.dll` with the EXTENSIBLE bug and
remain exposed regardless of what version of `Audio` is currently on
nuget.org. v2.4.1 is the republish that delivers the fix.

### Migration

- **Tool update required.** Run `dotnet tool update fieldcure-mcp-rag`
  (or refresh the auto-updater your host uses).
- **Re-index any KB that previously failed on a multi-channel WAV.** The
  earlier failure was a hard error at extraction, so the affected files
  appear in `index_timing.log` with `[FAILED:extract] ... — Input must be
  PCM or IEEE float`. After upgrading they re-extract on the next run.
- **No code or config changes** at the Mcp.Rag layer. `serve` / `exec` /
  `exec-queue` / `prune-orphans` surfaces unchanged from v2.4.0.

---

## v2.4.0 (2026-04-28)

### Changed

- **Adopted `FieldCure.DocumentParsers.Audio` v0.3.** The Whisper GPU runtimes
  (CUDA, Vulkan) are no longer in this tool's dependency graph at all.
  `DocumentParsers.Audio` v0.3 dropped its `Whisper.net.Runtime.Cuda` and
  `Whisper.net.Runtime.Vulkan` `PackageReference`s and instead downloads those
  binaries on first GPU use from
  [`fieldcure-whisper-runtimes`](https://github.com/fieldcure/fieldcure-whisper-runtimes)
  (manifest URL pinned in
  `GitHubReleasesWhisperRuntimeProvisioner.DefaultManifestUrl`,
  cache at `%LOCALAPPDATA%\FieldCure\WhisperRuntimes\`). The
  `Mcp.Rag.csproj` `Audio` package reference moves from `Version="0.*"` to
  `Version="0.3.*"` to pin the new contract.

- **CPU runtime stays bundled.** `Whisper.net.Runtime` (CPU) remains a direct
  `PackageReference` in `Mcp.Rag.csproj`. Reason is unchanged from v2.3.2 —
  `PackAsTool` strips `runtimes/<rid>/native/` from indirect dependencies, so
  the indirect path through `DocumentParsers.Audio` does not deliver the
  native binaries to the published tool. The 250 MB nuget.org cap that drove
  the earlier CPU-only trade-off is no longer a constraint: only the CPU
  runtime ever lands inside this nupkg.

### Why

Two effects compound:

1. **GPU acceleration becomes available for end users** automatically on
   Windows hosts whose driver matches the manifest's `minDriverVersion` gate
   (R525+ for CUDA 12.x; Vulkan has no driver-version policy). v2.3.x users
   were locked to CPU regardless of GPU presence because the GPU runtimes
   were excluded to fit under the package size cap.
2. **The tool nupkg gets a noticeable size reduction** because the indirect
   `Whisper.net.Runtime.Cuda.Windows` (~73 MB) and `Whisper.net.Runtime.Vulkan`
   (~47 MB) bytes are gone from the dependency graph entirely, even before
   PackAsTool's stripping kicks in.

### Migration

- **No code changes required for end users.** The `LazyAudioTranscriber`
  wrapper continues to use `WhisperEnvironment.RecommendModelSize()` exactly
  as before; v0.3's deprecated surface (`Probe()`, `CudaAvailable`) is not
  on Mcp.Rag's hot path.
- **First GPU transcription per host** triggers a one-time download:
  - `cuda` (win-x64) — ~75 MB
  - `vulkan` (win-x64) — ~49 MB

  SHA-256 hashes are verified at download time. Subsequent transcriptions
  reuse the cache. CPU transcription remains fully offline.
- **Air-gapped deployments** — set `FIELDCURE_WHISPER_RUNTIME_DIR` to a
  pre-staged directory containing `manifest.json` plus the
  `runtimes/<variant>/<rid>/<file>` tree. The Audio v0.3 provisioner
  performs zero network I/O when this variable is set. Layout reference is
  in the
  [`fieldcure-whisper-runtimes`](https://github.com/fieldcure/fieldcure-whisper-runtimes)
  README.
- **PoC archive.** The `poc/mcprag-rid-split` branch (RID-split investigation
  to fit GPU runtimes under the 250 MB cap) is obsoleted by Audio v0.3. The
  branch is left in place for decision-history purposes; no further work is
  planned on it.

---

## v2.3.2 (2026-04-27)

### Fixed

- **Whisper native runtime missing from v2.3.1 tool package.** The published
  v2.3.1 nupkg threw `Native Library not found in default paths. Verify you
  have included the native Whisper library in your application, or install
  the default libraries with the Whisper.net.Runtime NuGet.` on every audio
  file, blocking all KBs that contained `.mp3`/`.wav`/etc. Root cause: the
  native runtime files (`runtimes/<rid>/native/whisper.dll` and friends)
  ship inside the `Whisper.net.Runtime*` packages, which `DocumentParsers.Audio`
  references — but `PackAsTool` strips the `runtimes/<rid>/native/` folder
  from *indirect* dependencies during tool packaging, so the natives never
  reached the published tool's output directory.

  Fix: re-declare `Whisper.net.Runtime` as a direct `PackageReference` in
  `Mcp.Rag.csproj` (Windows-only conditional). When a package lists a
  runtime dependency *directly*, `PackAsTool` packs the natives into the
  tool output. **CPU runtime only** in this hotfix: the matching
  `Whisper.net.Runtime.Cuda` (~73 MB Windows + ~80 MB Linux) and
  `Whisper.net.Runtime.Vulkan` (~47 MB + ~47 MB) packages, when included
  alongside CPU, pushed the tool `.nupkg` past nuget.org's 250 MB hard
  limit (first push attempt returned `413 RequestEntityTooLarge`).
  Whisper transcription works on CPU; GPU acceleration is deferred until
  the tool is RID-split or GPU runtimes are factored into a separate
  optional install.

### Operational note

Users who already pulled v2.3.1 will see no Whisper transcription progress
on KBs containing audio. Symptoms in `index_timing.log`:

```
[FAILED:extract] some-file.mp3 — Native Library not found in default paths.
```

Upgrade to v2.3.2 (`dnx FieldCure.Mcp.Rag@2.3.2 ...` or refresh
auto-update). No data migration needed; rerunning the reindex on the
affected KB now picks up the audio files cleanly.

### Also in this release — `get_index_info` graceful failure

`get_index_info` previously had no top-level `try/catch`, so any internal
failure (e.g., `SqliteException` on a fresh KB whose `rag.db` was not yet
created, or a partially-initialized store after a crashed indexing run)
bubbled out and the MCP framework wrapped it in a plain-text
`"An error occurred invoking 'get_index_info'."` reply that hosts could
not parse as JSON. The host's catch then swallowed the failure silently,
leaving the user with no diagnostic.

The handler now wraps the body in a structured-error catch: on failure
it logs the exception with `ILogger<MultiKbContext>` and returns a valid
JSON payload of the shape `{ kb_id, status: "error", error: "<type>: <message>" }`.
Hosts that already handle the existing status values (`indexing`,
`queued`, `failed`, `ready`) need no change to keep working; new
consumers can branch on `"error"` for diagnostic surfacing.

### Already-correct behavior worth noting

Investigation into the v2.3.1 user impact initially raised concern about
queue dispatcher head-of-line blocking. Re-reading `ExecQueueRunner` —
its catch at the per-entry boundary already records `LastError` on the
failing entry and the loop's `WHERE LastError IS null` filter advances
to the next entry. No change needed; the impression of head-of-line
blocking was an artifact of the orchestrator never starting (Whisper
native DLL load failure happened during process init, before any entry
was picked up).

---

## v2.3.1 (2026-04-27)

### Changed

- Republished against `FieldCure.DocumentParsers.Audio` **v0.2.1**. The
  bundled Whisper model backing `WhisperModelSize.Large` moves from
  large-v3 to **large-v2** to escape long-form transcription repetition
  loops. No Mcp.Rag code changes; the indexing pipeline still asks the
  Audio package for the recommended size and `LazyAudioTranscriber`
  enforces it across the run.

### Why this is a republish, not just a transitive bump

Mcp.Rag ships as a `dotnet tool` (`PackAsTool=true`), which embeds the
exact `FieldCure.DocumentParsers.Audio.dll` resolved at pack time inside
its own .nupkg. v2.3.0 was packed against Audio v0.2.0, so existing v2.3.0
installations carry the large-v3-mapped Audio assembly and remain
exposed to the long-form loop. v2.3.1 is the republish that delivers the
fix; users must `dotnet tool update fieldcure-mcp-rag` (or equivalent)
to receive it.

The benchmark behind the v0.2.1 Audio decision is in the DocumentParsers
repository at `tools/AudioBenchmark/baseline-2026-04-27.md`.

### Migration

- **Tool update required.** Existing v2.3.0 installations keep the
  large-v3 model and stay exposed to the loop; run `dotnet tool update
  fieldcure-mcp-rag` (or equivalent) to pick up v2.3.1.
- **Cache cleanup** (one-time, optional): the previous
  `~/.fieldcure/whisper-models/ggml-large-v3.bin` (~3 GB) is no longer
  read. Delete manually if disk space matters. The replacement
  `ggml-large-v2.bin` downloads on the first audio-bearing indexing run
  after upgrade.
- **Re-index audio knowledge bases**: chunks transcribed before this
  upgrade still carry the loop output. The new chunks are stamped with
  the same `audio.model_size = Large` enum value, but the underlying
  Whisper weights differ. The chunk-level `audio.transcribed_at`
  timestamp is what distinguishes pre-upgrade from post-upgrade
  transcripts; query that field to find re-indexable chunks.
- **No CLI / config changes.** `serve` / `exec` / `exec-queue` surface
  unchanged.

## v2.3.0 (2026-04-27)

### Added

- **Windows-conditional audio transcription** via
  `FieldCure.DocumentParsers.Audio` 0.2.0. Files with extensions `.mp3`,
  `.wav`, `.m4a`, `.ogg`, `.flac`, and `.webm` now flow through the same
  indexing pipeline as documents — transcribed once, chunked, contextualized,
  embedded, and searchable alongside the rest of the corpus.
- **`LazyAudioTranscriber`** — defers ggml model download and Whisper
  runtime loading until the first audio file is processed. Mirrors
  `LazyOcrEngine`'s deferred-init pattern (thread-safe via
  `Lazy<Task<...>>` with `ExecutionAndPublication`).
- **Environment-aware model size selection** at startup. The transcriber
  calls `WhisperEnvironment.RecommendModelSize(QualityBias.Accuracy)` and
  enforces the resulting size across the run, so a single indexing pass
  produces a consistent corpus. The probe result and chosen model size
  are logged once to stderr for self-diagnosis.
- **Per-chunk audio metadata** (`audio.model_size`, `audio.transcribed_at`).
  Stamping every transcript chunk with the model used and a UTC ISO-8601
  timestamp lays the groundwork for selective reindexing after a hardware
  upgrade (e.g., "everything previously transcribed with `Tiny`").

### Platform support

- Audio support is **Windows-only** in this release, matching the
  Whisper.net + NAudio Media Foundation runtime constraints. On Linux
  and macOS the `.Audio` package is not referenced and audio files
  silently fall through with empty text. Cross-platform audio is
  tracked for a v1.x follow-up.

### Migration

- No code changes required for upstream consumers. The `serve` /
  `exec` / `exec-queue` CLI surface is unchanged; existing knowledge
  bases will simply pick up audio files on the next reindex if any
  are present in `SourcePaths`.
- First audio file triggers a one-time ggml model download into
  `{UserProfile}/.fieldcure/whisper-models/`. Plan for that bandwidth
  on the host that runs the first indexing pass.

## v2.2.0 (2026-04-25)

### Added

- **`GeminiEmbeddingProvider`** — native Google Gemini embedding API support.
  - Model: `gemini-embedding-2` (multilingual, 8k token input).
  - Asymmetric retrieval via `task_type` (`RETRIEVAL_DOCUMENT` for indexing,
    `RETRIEVAL_QUERY` for search) — Google reports 3–7% retrieval quality
    improvement over symmetric embedding.
  - Matryoshka dimension truncation via `output_dimensionality`
    (768 / 1536 / 3072). Recommended: **1536** (sweet spot — 50% storage
    of 3072 with identical MTEB score).
  - Client-side L2 normalization for sub-3072 outputs (the API
    pre-normalizes only the full-length vector).
  - Native endpoint (`/v1beta/models/{model}:embedContent`) rather than
    the OpenAI compatibility layer, which does not expose `task_type`.
- **`EmbeddingProviderFactory`** — central provider construction. Unifies
  the switch-on-string logic that was duplicated across `ExecQueueRunner`,
  `Program`, and `SearchDocumentsTool`.
- **`IEmbeddingProvider.EmbedQueryAsync`** — query-time embedding hook for
  asymmetric embedders. Default implementation delegates to `EmbedAsync`,
  so symmetric providers (Ollama, OpenAI text-embedding-3) are unaffected.

### Changed

- `HybridSearcher` now calls `EmbedQueryAsync` instead of `EmbedAsync` at
  the two query-side call sites. No behavior change for symmetric embedders.

### Migration

- No schema or config changes. Existing indexes remain valid.
- To adopt Gemini, add a KB with `provider: "gemini"` and re-index.
  Vector spaces are not interchangeable across embedders, so existing
  Ollama/OpenAI indexes must be rebuilt to switch.

### Configuration

```json
{
  "embedding": {
    "provider": "gemini",
    "model": "gemini-embedding-2",
    "apiKeyPreset": "Gemini",
    "dimension": 1536
  }
}
```

`GEMINI_API_KEY` is resolved from the `Gemini` (or `Google`) credential
preset via the existing `ApiKeyEnvironment` mapping.

| Dimension | MTEB | Storage | Use case |
|-----------|------|---------|-------------|
| 768       | 67.99 | 25%   | Storage-constrained |
| **1536**  | **68.17** | **50%** | **Recommended default** |
| 3072      | 68.17 | 100%  | Maximum quality (pre-normalized) |

---

## v2.1.1 (2026-04-20)

- Update MCP package metadata to the latest `server.json` format for NuGet and VS Code integration.

## v2.1.0 (2026-04-20)

### Changed

- **Cross-platform hardening of the OCR fallback** — the Tesseract-based OCR path is now enforced Windows-only at the package level instead of purely at runtime:
  - `FieldCure.DocumentParsers.Ocr` is referenced via an MSBuild `Condition="$([MSBuild]::IsOSPlatform('Windows'))"`.
  - A `WINDOWS_OCR` compile symbol gates the `AddOcrSupport` call and excludes `LazyOcrEngine.cs` from non-Windows builds.
  - Linux and macOS builds are pure managed code with no Tesseract native binaries. Scanned PDFs without a text layer yield empty text on those platforms; text-layer PDFs work normally everywhere.
- **DocumentParsers 1.x → 2.x** — PDF text extraction (PdfPig) is now part of the core `FieldCure.DocumentParsers` package; the dedicated `FieldCure.DocumentParsers.Pdf` reference is removed. `FieldCure.DocumentParsers.Pdf.Ocr` was renamed to `FieldCure.DocumentParsers.Ocr`. Public MCP tool surface is unchanged.
- **Credential resolution per mode** — `serve` (stdio) uses env var → MCP Elicitation with a session cache and a 2-attempt re-elicit cap. `exec` and `exec-queue` (headless batch) use env var only and soft-fail with a clear message when unset. See [ADR-001](https://github.com/fieldcure/fieldcure-assiststudio/blob/main/docs/ADR-001-MCP-Credential-Management.md) Phase 3a.

### Added

- **Ubuntu CI job** — `.github/workflows/ci.yml` now builds against both `windows-latest` and `ubuntu-latest` so the cross-platform guarantee is regression-tested every push.
- **XML doc tags** filled in across the source and test trees — every public and private method/type surface now carries `/// <summary>` where previously absent.

### Fixed

- **`SearchDocumentsTool` / `GetDocumentChunkTool`** class-level XML doc blocks had been placed after `[McpServerToolType]`; moved above the attribute so the compiler attaches them to the class. No runtime change.

---

## v2.0.0 (2026-04-17)

### Breaking

- **`unload_kb` tool removed** — KB cache eviction is now lazy (config.json deletion triggers automatic cleanup on next access)
- **API keys via environment variables** — `CredentialService` (Windows Credential Manager P/Invoke) removed. API keys are resolved from environment variables (e.g., `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`). AssistStudio injects them from PasswordVault into child process environments. Standalone callers set env vars directly.
- **`ChannelFactory.Create()` no longer requires `CredentialManager`** parameter

### Added

- **Queue-based indexing orchestrator** — all indexing requests flow through `start_reindex` MCP tool. Single orchestrator processes entries sequentially (no GPU contention). Scope merge rules (full ⊃ contextualization ⊃ embedding), force/deferred flags, PID-based lock with reuse defense.
- **`cancel_reindex` tool** — removes pending queue entries via MCP
- **`prune-orphans` CLI** — deletes orphan KB folders (GUID-named, no config.json). Protected folders (`.`, `_` prefix, `-backup-`) never touched.
- **`--sweep-all` flag for `exec-queue`** — processes deferred entries too (used at app shutdown)
- **Ollama native embedding provider** — `OllamaEmbeddingProvider` calls `/api/embed` (not `/v1/embeddings` shim) with `keep_alive` parameter. Requires Ollama 0.4.0+.
- **Ollama native contextualizer** — `OllamaChunkContextualizer` calls `/api/chat` with `keep_alive` and `options.num_ctx`. 10-minute HttpClient timeout for cold model loads.
- **`OllamaDefaults`** — `KeepAlive = "5m"`, `NumCtx = 8192` constants
- **`ProviderConfig.KeepAlive` / `NumCtx`** — per-KB override for Ollama parameters in config.json
- **`FolderClassification` + `Classify()`** — shared folder classification for `ListKbs` and `prune-orphans`
- **`MultiKbContext.BasePath`** — public read-only property
- **Lazy unload in `GetKb()`** — detects config.json deletion and auto-evicts cached instance

### Changed

- **`get_index_info` includes queue state** — `status` field (`ready`/`queued`/`indexing`/`failed`), `queue` object with position/deferred/last_error
- **`check_changes` / `get_index_info` promoted to AI-visible** — "Internal tool" / "Do not call" removed from descriptions
- **Orchestrator lock → separate file** — `orchestrator.lock` (PID + started_at) instead of embedded in queue JSON. PID reuse defense via `Process.StartTime` comparison.
- **Cross-platform** — `net8.0` TFM is no longer misleading; `#pragma warning disable CA1416` removed. Tesseract OCR loads lazily on first scanned PDF (Windows only); non-Windows platforms silently skip scanned pages but process text-layer PDFs normally.

### Removed

- `CredentialService.cs`, `ICredentialService.cs` (Windows Credential Manager P/Invoke)
- `EmbeddingProviderFactory.cs` (dead code, never called)
- `UnloadKbTool.cs`
- `DeferredQueueLock` from queue JSON (moved to `orchestrator.lock` file)

---

## v1.5.0 (2026-04-16)

### Added

- **Partial re-index (`--partial=contextualization|embedding`)** — when only the contextualizer or embedding model changes, OCR/chunking results are preserved and only downstream stages re-run. Scanned 600-page PDF: full re-index 20+ min, partial embedding ~2 min. Includes cancel-resume support (interrupted partial runs resume from remaining pending chunks) and cross-kind safety (mismatched partial flag on interrupted state produces a clear error with `--force` escape).
- **`search_mode` parameter on `search_documents`** — `"auto"` (default, hybrid when possible), `"bm25"` (keyword-only, no embedding), `"vector"` (semantic-only). Tool description instructs AI models not to override the default. `"vector"` with no embedding provider throws `InvalidOperationException`.
- **`unload_kb` tool** — evicts a cached KB instance and releases its SQLite connection so the host can delete KB files without retry loops. Idempotent, no-op if not loaded.
- **Chunker pre-validation** — re-splits any chunk exceeding a conservative character-based upper bound (default 4,000 chars, configurable via `embedding.max_chunk_chars`) during chunking, before embedding is attempted. Uses sentence boundaries when possible, hard cut otherwise. Binary-split fallback remains as safety net.
- **Embedding batch size resolution** — static lookup table maps common provider/model combinations to safe initial batch sizes (e.g., `ollama:qwen3-embedding:8b` = 64). Configurable via `embedding.batch_size`. Eliminates unnecessary first-call failures and binary-split activations. KB-3 (1025 chunks, Ollama): 210s with binary-split → 131s without.
- **Contextualization diagnostics** — multi-line log format when contextualization fails (`failed: N/M, falling back to raw`), `chunks_contextualized` column in `file_index` (schema v2), `total_chunks_contextualized` / `total_chunks_raw` / `files_contextualization_degraded` in `get_index_info`, and `is_contextualization_degraded` flag in `check_changes` (not folded into `is_clean`).
- **`PendingContextualization` chunk status (5)** — tracks chunks that need re-contextualization during partial re-index.
- **`GetPendingChunkCountsAsync`** — efficient kind-aware query for partial resume detection.

### Changed

- **Counter semantics cleanup** — in-memory counters split into per-run deltas (`indexedThisRun`, `skippedThisRun`) and DB state snapshots (`GetStateCountersAsync`). Summary log now shows two lines: `Run: indexed=N skipped=M ...` and `State: failed=N degraded=M ...`. Fixes misleading `degraded=0` on subsequent runs.
- **`IndexingEngine.RunAsync` signature** — now accepts optional `string? partial` parameter.
- **`EmbedWithBinarySplitAsync`** — accepts optional `batchSize` parameter; splits input into batch-sized windows before attempting API calls.
- **Cancel file cleanup** — moved to `Program.cs` top-level `finally` block for guaranteed cleanup regardless of exit path.
- **Schema version bump** — `TargetUserVersion` 1 → 2.

### config.json additions

| Field | Section | Description |
|-------|---------|-------------|
| `max_chunk_chars` | `embedding` | Max chars per chunk (default: 4000) |
| `batch_size` | `embedding` | Max chunks per API call (default: table lookup) |

---

## v1.4.2 (2026-04-15)

### Fixed

- **v1.4.0 silent data loss on first-time embedding failure** — closes the Known Issue recorded in v1.4.1. When a newly added file (no prior `file_index` row) failed at Stage 4 embedding, the old code caught `EmbeddingException` and called `MarkFileAsFailedAsync`, which was an `UPDATE`-only operation and a no-op when the row did not yet exist. The extracted text and contextualized chunks were silently dropped, the `last_partially_deferred_count` counter was incremented anyway, and the file became invisible to search until the user manually intervened. The next exec would re-run the full pipeline (OCR + contextualization + embedding) and hit the same error — an unbounded loop of wasted work. The 2-commit model described below prevents this structurally: chunks are persisted as `PendingEmbedding` *before* the embedding call is issued, so any Stage 4 outcome — success, per-chunk rejection, network failure, process crash, power loss — leaves the upstream work on disk for the deferred retry pass to pick up.

### Added

- **2-commit indexing pipeline** — every file goes through an explicit persist step between Stages 3 and 4. `SqliteVectorStore.PersistChunksAsPendingAsync` writes the full chunk list and a `file_index` row (status = `PartiallyDeferred`, `chunks_pending = N`) in a single transaction using `INSERT ... ON CONFLICT`, so the row is created whether or not a prior one existed. Only after this Commit 1 succeeds does the engine issue the embedding call. On success, `PromoteChunksToIndexedAsync` performs Commit 2a atomically: chunks flip to `Indexed`, embeddings are inserted, and the `file_index` row transitions to `Ready` (or `Degraded` when any chunk was rejected). On failure, Commit 1 state is left alone — the deferred retry pass will take over.
- **Binary-split per-chunk failure isolation (`EmbeddingBatchSplitter`)** — when the embedding provider rejects a batch, the splitter recursively halves until it either finds a working sub-batch or isolates the offending chunks at size-1. A single oversized chunk (e.g., a math-dense page that exceeds `text-embedding-3-large`'s 8192-token per-input limit) is marked `Failed` and the rest of the file is promoted to `Indexed`. The file's final status is `Degraded` — partially searchable rather than completely missing. The algorithm is provider-agnostic (driven by `EmbedBatchAsync` exceptions, no need to parse error strings) and costs nothing on the happy path: when all chunks succeed on the first try, the helper issues exactly one provider call. For N chunks with one failure, expect approximately `2 × log₂(N)` provider calls. Verified end-to-end against a real 850-chunk PDF: chunk 846 isolated in 21 provider calls across 10 split depths.
- **Automatic deferred retry pass (`RunDeferredRetryPassAsync`)** — at the end of every `exec` run, after the main loop, the engine walks every chunk still in `PendingEmbedding` state (grouped by source file) and re-runs Stage 4 via the same binary-split helper. This picks up files deferred this run AND files left deferred by previous runs, so the user no longer has to trigger retries manually. Per chunk, the retry budget is `IndexingEngine.MaxEmbeddingRetries = 3`; chunks that exhaust it are marked `Failed` without another provider call. On 401/403 the pass halts immediately and flags `ProviderHealth.EmbeddingUnavailable` so the rest of the queue is preserved for a future run with fixed credentials.
- **Safety guards on the binary split** — `MaxSplitDepth = 20` caps recursion against pathological inputs, and a top-level failure ratio exceeding 50 % triggers `DeferredFallback = true`, rolling the whole attempt back to `PendingEmbedding` so the next exec can retry with a clean slate. This distinguishes legitimate per-chunk rejections from provider-wide outages (quota, auth, model down).
- **Binary split diagnostic logging** — `[BinarySplit] start` / `done` lifecycle lines at Info level (silent on the happy path), per-depth `attempt` / `OK` / `FAILED` trace at Debug level, and per-chunk terminal failures at Warning level with the exact chunk id and provider error body. The `done` line carries `promoted`, `failed`, `fallback`, `depthMax`, and `providerCalls` so cost can be reconstructed from logs without a debugger. Absolute chunk-index offsets are used throughout (`range=[846..846]`, not `range=[0..0]`), so a failing chunk can be located in the source file directly.
- **`--verbose` / `-v` flag for `exec` mode** — raises the logger minimum level from Information to Debug so the per-depth binary-split trace becomes visible. Default stays at Information so normal runs remain quiet.
- **Status-aware hash-skip (`FileIndexStatusExtensions.ShouldSkipOnHashMatch`)** — the main loop's hash-skip decision is now centralized in one extension method. Every current status returns `true` (for different reasons: `Ready` and `Degraded` are fully persisted, `PartiallyDeferred` has Commit 1 state that the second pass will pick up, `Failed` and `NeedsAction` need explicit user action). The `default` arm returns `false` so any future status is forced to make an explicit choice. `NeedsAction` files surface through a dedicated counter instead of being lumped into `skipped`.
- **OCE `when`-guard on Stage 1 / Stage 4 exceptions** — `FileExtractionException` and `EmbeddingException` catches now use `when (cancellationToken.IsCancellationRequested)` to distinguish genuine user cancellation (which re-throws) from HttpClient timeouts disguised as `OperationCanceledException` (which are treated as normal stage failures). A 100-second OCR timeout used to abort the entire run; now it just fails the file and moves on.

### Changed

- **Deferred retry behavior (breaking behavior change)** — `PartiallyDeferred` files no longer require manual intervention. In v1.4.0 / v1.4.1, a deferred file stayed deferred until the host surfaced it and the user triggered a re-index; the deferred retry pass existed only as an API. In v1.4.2, every `exec` run automatically walks the pending chunks and retries embedding. Hosts that currently display "deferred files" as a user action should instead treat them as transient — they will resolve themselves on the next indexing run unless they exceed the retry budget. Combined with v1.4.1's `is_clean` extension (which already folded `is_schema_stale` into the clean check), the end-to-end semantic is: `check_changes.is_clean == true` now means both "no content changes" and "no schema migration pending", and the deferred retry pass handles the embed backlog in the background. AssistStudio is effectively the only consumer; host-side expectations around deferred files need to be revisited.
- **`MarkFileAsFailedAsync` return type** — now `Task<bool>` returning `true` when an existing row was updated and `false` when no row existed. Callers in `IndexingEngine.RunAsync` use the return value to log a "stuck-new-file" warning so the situation is visible even before the next `check_changes` call. Existing external callers that ignore the return value keep compiling; no source-breaking change for typical use.
- **Per-file `[Index]` log line** — now reads `(N failed via split)` instead of `(N rejected)` when the binary split isolated chunks, making the failure mode obvious at a glance in `index_timing.log`.
- **`IndexingEngine.PersistMetadataAsync` counter sanity check removed** — the check compared per-run delta counters against whole-DB totals, which are different quantities, and spuriously warned every second run after a legitimate `Degraded` transition. The v1.4.0 `MarkFileAsFailedAsync` no-op bug it was meant to catch is now prevented structurally by the 2-commit model, so the check has no remaining failure mode to guard against. The counter delta-vs-state semantic mismatch in the summary line is known and tracked for a later release.

### Known Limitations

- **~~Single chunk exceeding the embedding model's per-input token limit is marked `Failed`, not rechunked~~** — resolved in v1.5.0. `TextChunker` now enforces a `maxChars` upper bound (default 4000) that pre-splits oversized chunks before they reach Stage 4.

### Tests

- 19 new unit tests across `EmbeddingBatchSplitterTests` (14), store additions (PersistChunksAsPendingAsync / PromoteChunksToIndexedAsync / GetFileStateAsync / CountFilesByStatusAsync), and `FileIndexStatusExtensions`. Total 133 → 174 passing. Splitter coverage includes the happy path silence contract, single-bad-chunk isolation, cancellation rethrow, ArgumentException on mismatched counts, 401/403 bubble-up (must not be swallowed into per-chunk rejections), and the absolute-range-offset invariant for the per-chunk Warning line.
- End-to-end verification against `kb-2` with the real 1999 `Introduction to Electrodynamics.pdf` (850 chunks, chunk 846 over the token limit). Run 1: 11 previously-indexed files hash-skipped, 1999 PDF went through the full 2-commit pipeline, binary-split isolated chunk 846 in 21 provider calls at depth 10, file transitioned to `Degraded` with 849 / 850 chunks indexed. Run 2: all 12 files hash-skipped in 85 ms with no warnings — the expensive upstream work is preserved and will never be re-run for this file without `--force`.

---

## v1.4.1 (2026-04-15)

### Fixed

- **`MultiKbContext.ListKbs()` loose folder scanning** — previously any subfolder under the base path with a `config.json` was accepted as a KB, so user backup folders (e.g., `<kb-id>.backup-<ts>`) showed up as duplicate KBs in `list_knowledge_bases` and the host UI. Four guards are now applied per folder in order: (1) prefix filter — folders starting with `.` or `_` are skipped silently, (2) `config.json` existence, (3) `config.json` parseability (parse failures log a warning instead of being swallowed), (4) `config.Id` ↔ folder name match using `OrdinalIgnoreCase` (mismatches are the copy-backup signature and log a warning before skipping). Per-KB stats reads from `rag.db` are also wrapped — a corrupt DB no longer kills the entire listing, the affected KB just reports zeros with a warning.

### Added

- **`PRAGMA user_version` schema sentinel** — schema state is now recorded in SQLite's built-in `user_version` header field instead of being inferred from `PRAGMA table_info` alone. `SqliteVectorStore.TargetUserVersion = 1` is the target, written at the end of `InitializeSchema()` (which only runs when `readOnly == false`, so the "serve = reader, exec = writer" invariant is preserved). Legacy databases created before v1.4.1 report `user_version = 0` regardless of which actual schema columns they contain.
- **`SqliteVectorStore.GetUserVersion()`** — instance method that reads `user_version` from the store's connection. Cheap (microseconds) — `PRAGMA user_version` reads from page 0 which is already in memory after connection open. Prefer this when the caller already holds a store instance.
- **`SqliteVectorStore.ReadUserVersion(dbPath)`** — static helper that opens the database read-only, calls `GetUserVersion()`, and disposes. For diagnostic tools and tests that don't already have a store.
- **`KbSummary.SchemaVersion` / `KbSummary.IsSchemaStale`** — `list_knowledge_bases` now surfaces the schema version each KB was last tagged with. `IsSchemaStale == true` means the KB was indexed before v1.4.1 (or before whatever future version the code is built against). Stale KBs still serve search queries correctly; re-indexing triggers automatic migration through the exec path.
- **`list_knowledge_bases` response extension** — each KB entry now includes `schema_version` and `is_schema_stale` fields, and the top-level response carries `current_schema_version` so consumers can compare without hardcoding the target.
- **`check_changes` schema staleness fields** — the response now includes `is_schema_stale`, `kb_schema_version`, and `current_schema_version`. `is_schema_stale` is computed as `kb_schema_version < current_schema_version`. Tool description updated to explain what a stale KB means and that re-indexing triggers automatic migration.

### Changed

- **`check_changes.is_clean` semantics (soft breaking)** — `is_clean` now also requires `!is_schema_stale`. The first time a host running v1.4.1 or later calls `check_changes` against a KB that was indexed under v1.4.0 or earlier, the response will be `is_clean: false`, `is_schema_stale: true`, `kb_schema_version: 0` even when no files have changed. The fix is a single re-index per KB (the same remedy as for `is_prompt_stale`), which writes `user_version = 1` through the exec path's `InitializeSchema()`. AssistStudio is effectively the only consumer; host-side message branching will be updated in a follow-up release.
- **`MultiKbContext` constructor** — now accepts an optional `ILogger<MultiKbContext>` parameter (defaults to `NullLogger` when omitted, so existing tests and direct instantiation keep working). `Program.cs` serve mode registers the context through the DI container so the host's logging pipeline supplies the logger; the explicit `Dispose()` at shutdown is no longer needed because the host disposes singletons automatically.
- **`KbSummary` record** — new `SchemaVersion` and `IsSchemaStale` members. Positional record so any direct constructor call outside this assembly needs to supply the new values; in-tree callers are all updated.

### Tests

- 19 new unit and integration tests (total 114 → 133) covering the `user_version` sentinel round-trip, the read-only open invariant ("serve = reader" in test form), all four `ListKbs` guards (including the case-insensitive id match), and end-to-end `check_changes` response shape for both clean tagged and legacy untagged KBs. The integration tests exercise the full `CheckChangesTool → MultiKbContext → SqliteVectorStore` chain with real temp-folder fixtures, so the new JSON fields are validated at the wire level rather than only in isolation.

### Known Issues

- **Deferred embedding retry for first-indexed files is not persisted** — v1.4.0 introduced stage-level failure handling with a `PartiallyDeferred` counter and the infrastructure for retrying chunks whose embedding failed (`ChunkIndexStatus.PendingEmbedding`, `GetPendingEmbeddingChunksAsync`, `UpdateChunkStatusAsync`). That path works correctly when an *already indexed* file fails on re-indexing — `ReplaceFileChunksAsync`'s transaction rolls back and previous chunks are preserved. But when a *newly added* file fails embedding on its first pass, `IndexingEngine` catches `EmbeddingException` and calls `MarkFileAsFailedAsync`, which is an `UPDATE` against `file_index` and therefore a no-op when no prior row exists — the extracted text and contextualized chunks are silently dropped. The `last_partially_deferred_count` counter is still incremented, so the log and metadata will say "1 deferred" while the database contains nothing for that file. Result: the file is invisible to search and will be retried from scratch on every subsequent exec, hitting the same error. Tracked separately; will be addressed in a later release by (a) extending `IndexingEngine` to persist chunks with `PendingEmbedding` status when embedding fails and (b) wiring `GetPendingEmbeddingChunksAsync` / `UpdateChunkStatusAsync` into a deferred retry pass.

---

## v1.4.0 (2026-04-14)

### Added

- **Stage-level failure handling** — indexing pipeline now distinguishes extract, contextualize, and embed failures with dedicated exception types (`FileExtractionException`, `EmbeddingException`) and separate counters (`Failed`, `Degraded`, `PartiallyDeferred`)
- **Atomic file replace** — `ReplaceFileChunksAsync` wraps DELETE + INSERT + file_index upsert in a single SQLite transaction; embedding failure preserves previous data instead of causing data loss
- **Schema migration (v1.3 → v1.4)** — `AddColumnIfMissing` helper + `MigrateV04StatusColumns` adds 12 columns across `chunks`, `file_index`, and `_indexing_lock` tables with partial index `idx_chunks_status`
- **Chunk/file status enums** — `ChunkIndexStatus` (Indexed, IndexedRaw, PendingEmbedding, PendingExtraction, Failed) and `FileIndexStatus` (Ready, Degraded, PartiallyDeferred, NeedsAction, Failed)
- **`EnrichResult` return type** — `IChunkContextualizer.EnrichAsync` returns structured result with `IsContextualized`, `FailureReason`, `FailureType` instead of silently returning original text
- **Contextualization logging** — failed chunks now emit `LogWarning` with chunk index and exception details; `OperationCanceledException` propagated instead of swallowed
- **`IndexingResult` record** — `RunAsync` returns structured result replacing `int` exit code, with `Indexed`, `Skipped`, `Failed`, `Degraded`, `PartiallyDeferred`, `Duration`, and `ExitCode`
- **`ProviderHealth` enum** — tracks embedding/contextualizer availability in `_indexing_lock.provider_health` for real-time monitoring
- **Store API additions** — `MarkFileAsFailedAsync`, `GetPendingEmbeddingChunksAsync`, `UpdateChunkStatusAsync`, `ChunkWriteInfo`, `FileWriteInfo`, `PendingChunk`
- **Extended `UpdateProgress`** — now accepts `currentStage`, `failedCount`, `providerHealth` with COALESCE null-preserving semantics
- **Extended `get_index_info`** — 7 new response fields: `last_indexed_count`, `last_skipped_count`, `last_degraded_count`, `last_partially_deferred_count`, `last_run_duration_ms`, `last_run_completed_utc`, `last_provider_health`
- **Design doc** — `docs/rag-fallback-design.md` covering pipeline architecture, exec/SQLite IPC, chunk state machine, and failure handling

### Removed

- **`SetFileHashAsync`** — replaced by `FileWriteInfo` in `ReplaceFileChunksAsync` for atomic file_index updates

### Changed

- **`IChunkContextualizer.EnrichAsync`** signature — `Task<string>` → `Task<EnrichResult>` (breaking for custom implementations)
- **`IndexingEngine.RunAsync`** return type — `Task<int>` → `Task<IndexingResult>`
- **Contextualizer constructors** — `AnthropicChunkContextualizer` and `OpenAiChunkContextualizer` accept optional `ILogger` parameter

---

## v1.3.1 (2026-04-08)

### Fixed

- **Tesseract native DLL missing in dotnet tool** — upgraded `DocumentParsers.Pdf.Ocr` to 1.0.1 which includes `leptonica-1.82.0.dll` and `tesseract50.dll` via `buildTransitive/.targets`, fixing `DllNotFoundException` on server startup

---

## v1.3.0 (2026-04-08)

### Added

- **OCR fallback for scanned PDFs** — pages with no extractable text are rendered at 300 DPI and processed via Tesseract OCR (English + Korean, tessdata_fast)
- `FieldCure.DocumentParsers.Pdf.Ocr` 1.x dependency with engine pool for concurrent OCR

---

## v1.2.1 (2026-04-08)

### Fixed

- **`is_clean` excludes failed files** — failed files cannot be fixed by re-indexing, so `check_changes` no longer reports them as dirty; prevents misleading "re-index needed" status

---

## v1.2.0 (2026-04-08)

### Added

- **`check_changes` tool** — dry-run filesystem scan that compares source files against the index; returns added/modified/deleted/failed file counts and paths with `is_prompt_stale` and `is_clean` flags
- **`last_indexed_at` in `get_index_info`** — most recent indexing timestamp (ISO 8601 UTC) from `file_index`
- **Failed file tracking** — indexing persists `last_failed_count`, `last_failed_files`, and `last_failed_reasons` to `index_metadata`; `get_index_info` returns these fields; `check_changes` separates known-failed files from "added"

### Fixed

- **Progress double-increment** — `fileIndex++` was called in both try and finally blocks, causing progress to exceed total (e.g. 12/8 for 8 files)
- **Missing failure log** — failed file path and exception now written to `index_timing.log`

### Changed

- `IndexingEngine.SupportedExtensions` — now `internal static` for tool reuse

---

## v1.0.0 (2026-04-06)

### Stable Release

First stable release. Tool signatures, search behavior, and config.json schema are now covered by semver.

### Changed

- **Tool annotations completed** — all 4 tools declare `ReadOnly=true`, `Destructive=false`, `Idempotent=true`
- `FieldCure.DocumentParsers` 1.x — DOCX, HWPX, XLSX, PPTX, PDF text extraction
- `FieldCure.DocumentParsers.Pdf` 1.x

### Improved

- Added XML doc comments to all private/static methods (Program.cs, IndexingEngine, SqliteVectorStore)

---

## v0.12.2 (2026-04-03)

### Changed

- `ModelContextProtocol` 1.1.0 → 1.2.0

---

## v0.12.1 (2026-04-03)

### Fixed

- **Re-release of v0.12.0** — v0.12.0 nupkg contained stale v0.10.1 DLL due to `dotnet pack --no-build` using cached Release build
- **Publish script** — removed `--no-build` shortcut; now performs `dotnet clean` + full rebuild before packing

---

## v0.12.0 (2026-04-03)

### Breaking Changes

- **Multi-KB serve mode** — `serve --path <kb-path>` replaced with `serve --base-path <path>`, serving all KBs under the base path with a single process
- **`kb_id` parameter required** — `search_documents`, `get_document_chunk`, `get_index_info` now require a `kb_id` parameter
- **`RagContext` removed** — replaced by `MultiKbContext` (lazy-loading, concurrent KB management)

### Added

- **`MultiKbContext`** — manages multiple knowledge bases under a shared base path with `ConcurrentDictionary` lazy loading; auto-cleans cached instances for deleted KBs
- **`list_knowledge_bases` tool** — returns all available KBs with ID, name, file/chunk counts, and indexing status
- **Read-only `SqliteVectorStore`** — `readOnly` parameter skips schema initialization and opens DB in read-only mode for serve
- **`kb_id` in tool responses** — all tool responses now include `kb_id` for identification

### Changed

- `get_index_info` response: added `kb_name`, removed `default_prompt` and `contextualizer` fields

---

## v0.11.1 (2026-04-03)

### Changed

- `FieldCure.DocumentParsers` 0.3.x → 0.* (picks up 0.4.x with improved Markdown parsing)
- `FieldCure.DocumentParsers.Pdf` 0.2.x → 0.*

---

## v0.11.0 (2026-04-03)

### Breaking Changes

- **exec/serve dual-mode architecture** — single-process MCP server replaced with `exec` (headless indexing) + `serve` (search-only MCP server)
- **CLI interface changed** — `fieldcure-mcp-rag <context-folder>` → `fieldcure-mcp-rag serve --path <kb-path>` / `exec --path <kb-path>`
- **`index_documents` tool removed** — indexing is now handled by `exec` mode as a separate process
- **Environment variables removed** — `EMBEDDING_*` and `CONTEXTUALIZER_*` env vars replaced by `config.json` + PasswordVault credentials
- **DB file renamed** — `rag_index.db` → `rag.db`
- **Data path changed** — `{folder_hash}` based → `{kb-id}` (UUID) based, app creates the folder

### Added

- **`exec` mode** — headless indexing process with exit codes (0=success, 1=failure, 2=cancelled)
- **`serve` mode** — search-only MCP server (3 tools: `search_documents`, `get_document_chunk`, `get_index_info`)
- **`config.json`** — per-knowledge-base configuration (source paths, contextualizer/embedding model settings)
- **`CredentialService`** — Windows PasswordVault integration for API key resolution (shared with AssistStudio)
- **`IndexingEngine`** — extracted from `IndexDocumentsTool` with cancel file support and multiple source paths
- **Cancel file** — `{kb-path}/cancel` triggers graceful shutdown of exec process
- **Multiple source paths** — single knowledge base can index from multiple folder sources

---

## v0.10.1 (2026-03-28)

### Added

- **File count safety limits** — `index_documents` enforces soft limit (1,000 files → warning) and hard limit (10,000 files → error with hint to specify a subfolder)

---

## v0.10.0 (2026-03-27)

### Added

- **PDF indexing support** — `.pdf` files now indexed and searchable via `FieldCure.DocumentParsers.Pdf`; text extracted page-by-page with `## Page N` headers
- **Math equation indexing** — DOCX and HWPX math equations extracted as `[math: LaTeX]` blocks via `FieldCure.DocumentParsers` 0.3.x; equations are now searchable in the RAG index

### Changed

- `FieldCure.DocumentParsers` 0.2.x → 0.3.x
- `FieldCure.DocumentParsers.Pdf` 0.1.x → 0.2.x
- `ModelContextProtocol` 0.2.x → 1.1.0

---

## v0.9.0

Cross-process indexing lock for multi-tab safety.

- **`_indexing_lock` table** — SQLite singleton-row mutex prevents concurrent indexing from multiple processes on the same DB
- **`AcquireLock` / `ReleaseLock`** — `index_documents` acquires lock before indexing, releases in `finally`; returns error if another live process holds the lock
- **`UpdateProgress`** — per-file progress written to lock row during indexing
- **Stale lock detection** — both `AcquireLock` and `GetLockInfo` check if lock holder PID is alive; dead processes are auto-cleaned
- **`get_index_info` returns `is_indexing`** — includes `indexing_progress` object (`current`, `total`, `pid`) when another process is indexing
- **Exception-free PID check** — uses `Process.GetProcesses()` instead of `Process.GetProcessById()` to avoid first-chance `Win32Exception`

## v0.8.0

MCP progress notifications for indexing operations.

- **Indexing progress notifications** — `index_documents` reports per-file progress via MCP `notifications/progress` protocol; clients passing a `progressToken` receive `(current, total, message)` updates for determinate progress bar display
- **`IProgress<ProgressNotificationValue>` injection** — SDK auto-binds the progress reporter; no manual `McpServer` handling needed in the tool

## v0.7.0

Parallel contextualization, shared constants, and indexing diagnostics.

- **Parallel chunk contextualization** — `Parallel.ForEachAsync` with `MaxDegreeOfParallelism=4` for contextualizer API calls; ~4.5x speedup (172s → 38s on 41 chunks with gpt-4o-mini)
- **NullChunkContextualizer fast path** — skips parallel overhead when no contextualizer model is configured
- **Indexing timing log** — per-file and per-stage diagnostics written to `{dataRoot}/index_timing.log` (parse, chunk, context, embed, store)
- **Shared `DefaultMaxTokens` constant** — contextualizer max_tokens raised from 300 → 500 to prevent KEYWORDS truncation on bilingual outputs
- **Renamed `SystemPrompt` → `DefaultSystemPrompt`** — clearer naming for the built-in default prompt constant

## v0.6.0

Per-folder system prompt with DB persistence and stale-index detection.

- **`index_metadata` table** — key-value store in SQLite for index configuration
- **Per-folder system prompt** — `index_documents` accepts optional `system_prompt` parameter; stored in DB per folder
- **Prompt priority chain** — tool parameter > DB stored value > env var > built-in default
- **Stale-index detection** — `effective_prompt_hash` stored in DB; `get_index_info` returns `is_prompt_stale` when built-in prompt has been updated since last indexing
- **`get_index_info` tool** — returns folder stats, prompt configuration, and stale detection flag (internal, for host application use)
- **`IChunkContextualizer.SystemPrompt` property** — runtime-settable on all contextualizer implementations
- **Smart default handling** — only custom prompts stored in DB; null = use built-in default (code updates auto-apply on re-index)

## v0.5.0

- **Improved default contextualization prompt** — bilingual keyword extraction (original language + English), domain-specific concept extraction, grammatical suffix removal with concrete example
- **Custom system prompt** — `CONTEXTUALIZER_SYSTEM_PROMPT` environment variable for domain-specific keyword extraction (empty = built-in default)

## v0.4.0

- **Fix DocumentParsers dependency** — pin to `0.2.*` to ensure XLSX/PPTX/HWPX parsers are included (v0.3.0 bundled v0.1.0 due to `Version="*"` resolving before NuGet indexing)

## v0.3.0

AI-powered chunk contextualization and .NET 8.0 migration.

- **Chunk contextualization** — AI-generated context descriptions and normalized keywords per chunk during indexing (`IChunkContextualizer`)
- **OpenAI-compatible contextualizer** — supports Ollama, OpenAI, Groq, and any `/v1/chat/completions` endpoint (`OpenAiChunkContextualizer`)
- **Anthropic contextualizer** — supports Claude Haiku, Sonnet, Opus via `/v1/messages` endpoint (`AnthropicChunkContextualizer`)
- **Enriched search** — FTS5 and vector embedding use enriched text; original content preserved for responses
- **Graceful degradation** — contextualization disabled when `CONTEXTUALIZER_MODEL` is empty (v0.2.0 behavior)
- **Error tolerance** — AI failures fall back to original chunk text without interrupting indexing
- **v0.2.0 DB migration** — `enriched` column added automatically via `ALTER TABLE`; existing chunks initialized with original content
- **net9.0 → net8.0** — unified target framework with FieldCure.DocumentParsers
- **XLSX / PPTX support** — via FieldCure.DocumentParsers v0.2.0 (Excel spreadsheets and PowerPoint presentations)

## v0.2.0

Hybrid BM25 + Vector search with Reciprocal Rank Fusion.

- **FTS5 BM25 full-text search** — SQLite FTS5 trigram index for keyword matching, Korean whitespace tokenization
- **Reciprocal Rank Fusion (RRF)** — k=60 fusion of BM25 and vector results into a unified ranked list
- **HybridSearcher** — automatic search mode selection (hybrid / bm25_only / vector_only) with graceful degradation
- **Embedding server optional** — BM25 keyword search works without any embedding server configured (NullEmbeddingProvider)
- **Data path migration** — index storage moved from `{contextFolder}/.rag/` to `%LOCALAPPDATA%\FieldCure\Mcp.Rag\{hash}\`; existing indices auto-migrated
- **search_documents enhanced** — returns `search_mode` field indicating which search strategy was used
- **Default threshold lowered** — similarity threshold 0.5 → 0.3 for better recall

## v0.1.0

Initial release.

- **MCP RAG server** — stdio transport, 3 tools (`index_documents`, `search_documents`, `get_document_chunk`)
- **Document parsing** — DOCX, HWPX via FieldCure.DocumentParsers; TXT, MD natively; auto-extends with new parsers
- **Incremental indexing** — SHA256 change detection, orphan cleanup for deleted files
- **Korean-optimized chunking** — regex-based sentence boundary splitting (습니다./해요./하죠.), decimal protection (3.14), parenthesis-aware splitting
- **OpenAI-compatible embeddings** — supports Ollama, LM Studio, OpenAI, Azure OpenAI, Groq, Together AI
- **SQLite vector store** — WAL mode, SIMD-accelerated cosine similarity, MemoryMarshal serialization
- **42 unit tests** — chunking, storage, embedding, document parsing with real test files
