# PoC: Mcp.Rag RID-split — **Closed (math-disconfirmed) → pivoted to runtime-download**

**Branch:** `poc/mcprag-rid-split` (do not merge)
**Status:** Closed. Numbers ruled out the original direction. Real fix lives in a separate, ongoing PoC: `DocumentParsers.Audio` v0.3 with runtime-download (branch `poc/audio-runtime-download` in `fieldcure-document-parsers`).

## TL;DR

> Hypothesis: rejigger `Mcp.Rag.csproj` packing flags (RuntimeIdentifier / RuntimeIdentifiers / ExcludeAssets / publish-then-pack) to fit `Whisper.net.Runtime` + `.Cuda` + `.Vulkan` win-x64 natives + multi-RID Sqlite under nuget.org's 250 MB cap, restoring v2.3.1's GPU acceleration that v2.3.2 had to drop. Local 4-variant pack inspection killed the hypothesis on **arithmetic**, not on tooling: even the best recipe (Whisper contentFiles+build excluded, win-x64 manual native inject, Sqlite preserved as cross-platform requires) totals ~258 MB — **8 MB over** the cap. Sqlite native binaries (~120 MB across all RIDs) are non-negotiable: Mcp.Rag is cross-platform-by-design and uses SQLite FTS5 as its core storage on every host. So GPU recovery via single-nupkg path is mathematically impossible without trading away Linux/macOS support. Pivot: GPU runtimes get downloaded at first GPU use rather than bundled; ownership lives in `DocumentParsers.Audio` v0.3.

## Hypothesis going in

The Mcp.Rag v2.3.2 hotfix shipped CPU-only Whisper after `Whisper.net.Runtime` + `.Cuda` (~73 MB win + ~80 MB linux) + `.Vulkan` (~47 MB + ~47 MB) PackageReferences pushed the dotnet-tool nupkg past nuget.org's 250 MB hard limit (HTTP 413 on `nuget push`). v2.3.2's release notes flagged GPU recovery as deferred until "we either RID-split the tool publish or move runtimes to a separate optional install".

This PoC tested the first option. Four MSBuild-based variants were planned, gated on `$(PoCVariant)` so production behavior was unaffected:

| Variant | Modification |
|---------|--------------|
| **α** | `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` (singular) + Cuda + Vulkan re-added |
| **β** | `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>` (plural) + Cuda + Vulkan re-added |
| **γ** | `ExcludeAssets="contentFiles;build"` on `Whisper.net.Runtime.*` + manual win-x64 native inject + Cuda + Vulkan re-added |
| **δ** | CLI-driven: `dotnet pack -r win-x64` (no csproj-side RID set) |

## What disconfirmed it

### Local pack inspection on `windows-latest`-equivalent

| Variant | nupkg size | Verdict |
|---------|-----------:|---------|
| Baseline (current v2.3.2 CPU-only) | **138 MB** | ✓ |
| α (singular RID + Cuda + Vulkan) | **490 MB** | ✗ Worse than baseline |
| γ (ExcludeAssets contentFiles+build, no manual inject yet) | **128 MB** | ✗ All Whisper natives stripped (regresses v2.3.1 "Native Library not found" bug) |

β / δ not run — α and γ already revealed the structural problem.

### Why α went the wrong way (490 MB)

`Whisper.net.Runtime.*` packages have **non-standard packaging**: they distribute native binaries through three parallel mechanisms simultaneously, only one of which respects RID resolution:

1. Standard `runtimes/<rid>/native/` — RID-aware. RuntimeIdentifier filters this. Small win.
2. Flat `content/` and `contentFiles/any/net8.0/` with **all platforms' DLLs/SOs/dylibs dumped together** — ignored by RID resolution, flows to the publish output regardless. Source of bulk.
3. `Whisper.net.Runtime.Cuda` / `.Vulkan` use **pseudo-RID layout** `runtimes/cuda/<rid>/` and `runtimes/vulkan/<rid>/`. NuGet doesn't recognize "cuda" / "vulkan" as RIDs, so RID resolution is a no-op here too.

Setting `RuntimeIdentifier=win-x64` shrinks (1) but does nothing for (2) and (3). The 490 MB is `runtimes/<rid>/native/` correctly trimmed but `content/` + `contentFiles/` + `runtimes/{cuda,vulkan}/<rid>/` carrying 80 MB Linux Cuda + 47 MB Linux Vulkan etc. unchanged.

### Why γ would still fail the math even if executed cleanly

`ExcludeAssets="contentFiles;build"` does suppress mechanism (2). With the corresponding `<None Pack="true">` manual inject added (which mis-fired in the local run; debuggable but not pursued), the recipe in principle yields:

```
v2.3.2 baseline (multi-RID Sqlite + everything except GPU)   = 138 MB
+ Whisper.net.Runtime CPU win-x64 native                     ≈   7 MB
+ Whisper.net.Runtime.Cuda win-x64 native                    ≈  73 MB
+ Whisper.net.Runtime.Vulkan win-x64 native                  ≈  47 MB
                                                              -------
                                                              ≈ 265 MB  > 250 MB cap
```

15 MB over.

The earlier suggestion "trim Sqlite to win-x64 to free headroom" was retracted: Mcp.Rag is cross-platform (the existing `Condition="!IsOSPlatform('Windows')"` blocks in the csproj prove the design intent — Linux/macOS hosts run `serve` mode without Audio). Sqlite native must stay multi-RID; trimming it would break Linux Mcp.Rag servers, which is the actual core use case for the cross-platform `serve` runner.

### What can fit (single-GPU compromise, not pursued)

If only one of Cuda / Vulkan is included:

```
138 + 7 + 73 (Cuda only)    = 218 MB ✓ fits, ~32 MB headroom
138 + 7 + 47 (Vulkan only)  = 192 MB ✓ fits, ~58 MB headroom
138 + 7 + 73 + 47 (both)    = 265 MB ✗ over by 15 MB
```

Cuda-only would cover NVIDIA users (largest segment), leave AMD/Intel/Apple GPU users on CPU (current v2.3.2 state). This is a viable v2.4.0 path but locks Vulkan users out indefinitely. Rejected as the durable answer.

## What replaced it

Decision: **download GPU runtimes at first GPU use rather than bundle.** Ownership goes to `DocumentParsers.Audio` v0.3 (one level down from Mcp.Rag) so the same mechanism benefits any consumer of the Audio package, not just Mcp.Rag.

PoC 0 (desk research, completed before pivot) validated two prerequisites:

- **`Whisper.net` 1.9.0 supports custom runtime path** via `RuntimeOptions.LibraryPath` static property. Verified against the local `D:\Codes\whisper.net` repo at tag `1.9.0`: `NativeLibraryLoader.GetRuntimePaths` prepends the directory of `RuntimeOptions.LibraryPath` to its search roots, then probes `runtimes/cuda/win-x64/`, `runtimes/vulkan/win-x64/`, `runtimes/win-x64/` etc. for each `RuntimeLibrary` in `RuntimeOptions.RuntimeLibraryOrder`. Setting `LibraryPath` to a directory under `%LOCALAPPDATA%\FieldCure\WhisperRuntimes\` before any `WhisperFactory.From*()` call routes the loader there.
- **NVIDIA CUDA Toolkit redistributable license permits self-hosting** the relevant DLLs (`cudart64_*.dll`, `cublas64_*.dll`, `cublasLt64_*.dll`, etc.) on GitHub Releases under our org. Reference: NVIDIA CUDA Toolkit EULA Attachment A. Conditions: (a) "material additional functionality" beyond CUDA — Mcp.Rag/Audio satisfies, (b) binaries unmodified, (c) license header preserved. **Critical exclusion: `nvcuda.dll` is NOT redistributable** — it ships with the user's NVIDIA driver and lives in `C:\Windows\System32\`. Capability probing must distinguish "driver present" (detect via `nvcuda.dll` existence) from "redistributable provisioned" (detect via cudart64 in our managed dir).

PoC moved to `poc/audio-runtime-download` branch in `fieldcure-document-parsers`. Three-phase capability lifecycle (Detect → Provision → Activate) documented there.

## Artifacts in this branch

- `src/FieldCure.Mcp.Rag/FieldCure.Mcp.Rag.csproj` — diff adds inert `$(PoCVariant)` conditional blocks (variants α/β/γ + Cuda/Vulkan re-add). Property unset = original v2.3.2 behavior, so even an accidental merge to main would not affect production packs. **Still: do not merge.**
- `poc/mcprag-rid-split/README.md` — this document.

No GitHub Actions workflow was authored — local smoke-test on the cheapest variant (α) disconfirmed the hypothesis before CI scaffolding made sense. Same lesson the PoC 1 (`audio-rid-split`) close-out applied: smoke-test the cheapest variant locally before authoring a CI matrix.

## Lessons

1. **`Whisper.net.Runtime.*` packaging is non-standard in three parallel ways.** Anyone reasoning about Whisper redistribution must know this: `runtimes/<rid>/native/` (RID-aware), `content/`+`contentFiles/` (not RID-aware), and `runtimes/{cuda,vulkan}/<rid>/` (pseudo-RID). Standard MSBuild RID flags only address the first.
2. **Sqlite is not dead weight in a cross-platform server.** When sizing a tool nupkg under nuget.org's cap, native dependencies that look ignorable on the developer's machine may be load-bearing on a different deployment target. Distinguish by use case, not by platform of the developer.
3. **The 250 MB cap is a real architectural constraint, not a tooling annoyance.** When the math doesn't close, no clever packing flag rescues it. Pivot the responsibility, not the packing.
4. **Two PoCs collapsed into one once misaligned premises were dropped.** PoC 1 (`audio-rid-split` in DocumentParsers) thought Audio's nupkg carried dead bytes — wrong, Audio is managed-only. PoC 2 (this) thought RID-split could fit Whisper + Cuda + Vulkan + Sqlite — wrong, the math doesn't close. Both pointed at the same real solution (runtime-download in Audio v0.3), reachable only after both negative results.
