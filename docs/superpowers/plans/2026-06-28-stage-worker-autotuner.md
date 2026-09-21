# Stage Worker Autotuner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a read-only hybrid benchmark that recommends independent worker counts for every parallel VDF stage and support manual/Web-imported worker profiles at runtime.

**Architecture:** Add versioned worker-profile and stage-resolution types to Core, refactor initial scanning into metadata/frame/audio phases, add a direct `VDF.Benchmarks` autotune probe over real and synthetic corpora, and expose all settings plus atomic profile import/export in VDF.Web. Existing global parallelism remains the compatibility fallback when a stage value is zero.

**Tech Stack:** .NET 10, C#, System.Text.Json source generation, Blazor Server, xUnit, FFmpeg/FFprobe, existing VDF benchmark probes.

---

### Task 1: Core worker profile and resolution

**Files:**
- Create: `VDF.Core/WorkerProfile.cs`
- Modify: `VDF.Core/Settings.cs`
- Modify: `VDF.Core/Utils/CoreJsonContext.cs`
- Create: `VDF.Core.Tests/WorkerProfileTests.cs`

- [ ] **Step 1: Write failing tests**

Test:

```csharp
[Theory]
[InlineData(0, -1, 24)]
[InlineData(-1, 6, 24)]
[InlineData(8, -1, 8)]
[InlineData(-2, 12, 12)]
public void ResolveStageParallelism_UsesInheritanceAndAllCoreSemantics(
    int stage, int global, int expected) {
    Assert.Equal(expected, WorkerParallelism.Resolve(stage, global, 24));
}
```

Also test profile JSON round-trip, schema rejection, 3%-near-best selection, and old Settings JSON leaving new fields at zero.

- [ ] **Step 2: Verify tests fail**

Run:

```bash
dotnet test VDF.Core.Tests/VDF.Core.Tests.csproj -c Release \
  --filter FullyQualifiedName~WorkerProfileTests
```

Expected: compile failure because `WorkerProfile` and `WorkerParallelism` do not exist.

- [ ] **Step 3: Implement minimal Core model**

Create:

```csharp
public enum WorkerStage {
    Metadata, FrameHash, AudioHash, VisualCompare, PHashCompare,
    PartialIndex, PartialExact, PartialVisualSource,
    PartialVisualClip, Thumbnail
}

public sealed class WorkerStageMeasurement {
    public WorkerStage Stage { get; set; }
    public int Workers { get; set; }
    public double ItemsPerSecond { get; set; }
    public double ElapsedSeconds { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public string ResultChecksum { get; set; } = string.Empty;
    public bool Valid { get; set; }
}

public sealed class WorkerProfile {
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int VisibleCpuCount { get; set; }
    public string Preset { get; set; } = "standard";
    public bool Completed { get; set; }
    public int MetadataWorkers { get; set; }
    public int FrameHashWorkers { get; set; }
    public int AudioHashWorkers { get; set; }
    public int VisualCompareWorkers { get; set; }
    public int PHashCompareWorkers { get; set; }
    public int PartialIndexWorkers { get; set; }
    public int PartialExactWorkers { get; set; }
    public int PartialVisualSourceWorkers { get; set; }
    public int PartialVisualClipWorkers { get; set; }
    public int ThumbnailWorkers { get; set; }
    public List<WorkerStageMeasurement> Stages { get; set; } = new();
}
```

Add stage fields to `Settings`, all defaulting to zero except the existing Partial visual default.

- [ ] **Step 4: Run Core tests**

Expected: all `WorkerProfileTests` pass.

### Task 2: Autotune primitives

**Files:**
- Create: `VDF.Benchmarks/Autotune/AutotunePreset.cs`
- Create: `VDF.Core/WorkerAutotune.cs`
- Create: `VDF.Benchmarks/Autotune/MediaSampler.cs`
- Create: `VDF.Core.Tests/WorkerAutotuneTests.cs`

- [ ] **Step 1: Write failing tests**

Pin the Core sweep/recommendation API:

```csharp
Assert.Equal(
    new[] { 1, 24, 2, 20, 4, 16, 6, 12, 8 },
    WorkerSweep.ForCpuCount(24));
```

Test deterministic size-stratified path selection and “within 3% chooses lowest worker”.

- [ ] **Step 2: Implement preset budgets and sweep**

Use exact standard budgets from the approved design. `WorkerRecommendation.Select`
must reject mismatched checksums and runs with fewer than 80% of reference successes.

- [ ] **Step 3: Run tests**

Expected: deterministic recommendations on repeated calls.

### Task 3: Real-media stage executor

**Files:**
- Create: `VDF.Benchmarks/Autotune/RealMediaStageRunner.cs`
- Create: `VDF.Benchmarks/Autotune/StageMeasurement.cs`
- Modify: `VDF.Benchmarks/Scenarios/VideoCorpus.cs`
- Add integration tests: `VDF.IntegrationTests/Benchmarks/StageAutotuneIntegrationTests.cs`

- [ ] **Step 1: Write failing integration tests**

Using generated H.264 fixtures, assert metadata, frame hashes, audio fingerprints, and thumbnails have identical per-file checksums at 1 and 2 workers.

- [ ] **Step 2: Implement read-only runners**

Call existing APIs:

```csharp
FFProbeEngine.GetMediaInfo(path, false);
FfmpegEngine.GetGrayBytesFromVideo(entry, positions, 0, false);
ChromaprintEngine.ExtractFingerprint(path, false, cancellationToken);
FfmpegEngine.ExtractThumbnailJpeg(path, position, 480, false, 85);
```

Never call `DatabaseUtils.SaveDatabase` and never create files below the media root.

- [ ] **Step 3: Add pilot-based sample sizing**

Measure a deterministic eight-file prefix at one worker, then choose the largest
prefix predicted to fit the stage budget.

- [ ] **Step 4: Run integration tests**

Expected: checksums match and media directory contents are unchanged.

### Task 4: Synthetic comparison stage executor

**Files:**
- Refactor: `VDF.Benchmarks/Scenarios/ComparePhaseProbe.cs`
- Refactor: `VDF.Benchmarks/Scenarios/PartialComparePhaseProbe.cs`
- Create: `VDF.Benchmarks/Autotune/SyntheticStageRunner.cs`

- [ ] **Step 1: Extract reusable deterministic corpus factories**

Keep existing seeds and expected duplicate/group/recall sentinels.

- [ ] **Step 2: Sweep visual gray, pHash, Partial index, and Partial exact**

Use one warmup plus three measured iterations; compare every result against the
one-worker reference.

- [ ] **Step 3: Verify existing probes still run**

Run both original direct probes and confirm result counts remain unchanged.

### Task 5: Autotune command and report

**Files:**
- Create: `VDF.Benchmarks/Scenarios/StageWorkerAutotuneProbe.cs`
- Modify: `VDF.Benchmarks/Program.cs`
- Create: `VDF.Benchmarks.Tests/VDF.Benchmarks.Tests.csproj`
- Create: `VDF.Benchmarks.Tests/StageWorkerAutotuneProbeTests.cs`
- Modify: `VideoDuplicateFinder.sln`

- [ ] **Step 1: Add parser tests for required options**

Cover missing media directory, invalid preset, output path, thumbnail count, and Ctrl+C.

- [ ] **Step 2: Implement orchestration**

Accept:

```text
--probe-stage-autotune --media-dir PATH --preset quick|standard|deep
--output PATH --thumbnail-count N
```

Print each measurement immediately, write partial JSON with `Completed=false` on
cancellation, and return nonzero when no importable profile can be produced.

- [ ] **Step 3: Run on generated fixture directory**

Expected: valid schema-1 JSON and at least one recommendation per stage.

### Task 6: Phase-specific runtime settings

**Files:**
- Modify: `VDF.Core/ScanEngine.cs`
- Modify: `VDF.Core/Settings.cs`
- Modify: `VDF.Core/ComparisonProgressChangedEventArgs.cs`
- Add tests: `VDF.Core.Tests/StageParallelismTests.cs`

- [ ] **Step 1: Write failing resolution tests**

Verify each ScanEngine stage uses its configured field, zero inherits global, and
Partial visual remains independently capped.

- [ ] **Step 2: Add resolved properties**

Add properties such as:

```csharp
int MetadataParallelism => WorkerParallelism.Resolve(
    Settings.MetadataMaxDegreeOfParallelism,
    Settings.MaxDegreeOfParallelism);
```

- [ ] **Step 3: Add independent gates to `GatherInfos`**

Keep the per-file pipeline, run its outer asynchronous loop at the maximum of
the three stage limits, and guard metadata, frame hashing, and audio hashing with
separate asynchronous `SemaphoreSlim` gates. Preserve pause, cancellation,
failure flags, existing cache checks, and checkpoints.

- [ ] **Step 4: Route comparison and thumbnails**

Use Visual/PHash, Partial index/exact, Partial visual, and Thumbnail settings at
their matching `Parallel.For` call sites.

- [ ] **Step 5: Run Core and integration tests**

Expected: old settings produce identical results; fixed videos produce identical
hashes and fingerprints before/after refactor.

### Task 7: Web persistence and manual settings

**Files:**
- Modify: `VDF.Web/Services/WebSettingsService.cs`
- Modify: `VDF.Web/Components/Pages/Settings.razor`
- Modify: `VDF.Web/WebJsonContext.cs`
- Create: `VDF.Web.Tests/VDF.Web.Tests.csproj`
- Create: `VDF.Web.Tests/WorkerProfileImportTests.cs`
- Modify: `VideoDuplicateFinder.sln`

- [ ] **Step 1: Add all stage fields to Web DTO**

Load and save values with valid range `-1` or `0..256`.

- [ ] **Step 2: Add manual Performance Workers section**

Render one input per stage, inheritance text, and resolved worker count.

- [ ] **Step 3: Implement atomic profile import**

Read browser `InputFile`, deserialize `WorkerProfile`, validate all values and
schema before mutating Settings, then apply recommendations together.

- [ ] **Step 4: Implement profile export**

Download a schema-1 JSON containing current manual values and available benchmark
metadata.

- [ ] **Step 5: Verify invalid import leaves settings unchanged**

Test malformed JSON, incomplete profile, unsupported schema, and CPU-count warning.

### Task 8: Publish benchmark in the Web image

**Files:**
- Modify: `VDF.Web/Dockerfile`
- Modify: `README.md`

- [ ] **Step 1: Restore and publish `VDF.Benchmarks` in build stage**

Publish to `/app/bench` and copy into the final image without changing the normal
Web entrypoint.

- [ ] **Step 2: Verify runtime command**

Run the command from the design with read-only media and writable output mounts.
Expected: `/output/worker-profile.json` exists and normal Web startup is unchanged.

### Task 9: Final verification

- [ ] Run Core, CLI, Integration, and Web builds in .NET 10 Docker.
- [ ] Run quick autotune on generated fixtures.
- [ ] Run standard autotune on Synology real media.
- [ ] Import the profile through Web and verify every resolved worker.
- [ ] Compare duplicate groups before/after applying the profile.
- [ ] Record total benchmark duration and recommendations for the 24-vCPU host.
