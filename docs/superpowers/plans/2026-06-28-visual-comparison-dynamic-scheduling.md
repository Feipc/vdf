# Visual Comparison Dynamic Scheduling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep all configured visual-comparison workers busy when thousands of videos remain in one duration bucket without changing comparison results.

**Architecture:** Preserve the existing duration-bucket candidate index and `CompareEntry` hot path. Replace whole-bucket scheduling with a no-buffering dynamic partitioner over video indices so every video is independently stealable.

**Tech Stack:** C# 13, .NET 10, `System.Collections.Concurrent.Partitioner`, xUnit, existing VDF synthetic benchmarks.

---

### Task 1: Dynamic visual work partitioner

**Files:**
- Create: `VDF.Core/VisualComparisonWorkPartitioner.cs`
- Create: `VDF.Core.Tests/VisualComparisonWorkPartitionerTests.cs`

- [ ] **Step 1: Write the failing coverage and concurrency tests**

Create tests that request 5,001 indices, process the returned partitioner with
four workers, and assert every index is returned exactly once. Use a four-party
gate in the action and assert peak active work is at least four, proving one
logical source bucket does not serialize the work.

- [ ] **Step 2: Run the focused tests and verify the helper is missing**

Run:

```bash
dotnet test VDF.Core.Tests/VDF.Core.Tests.csproj -c Release \
  --filter FullyQualifiedName~VisualComparisonWorkPartitionerTests
```

Expected: compilation failure because `VisualComparisonWorkPartitioner` does
not exist.

- [ ] **Step 3: Implement the no-buffering index partitioner**

Implement:

```csharp
internal static class VisualComparisonWorkPartitioner {
    public static OrderablePartitioner<int> Create(int count) =>
        Partitioner.Create(
            Enumerable.Range(0, Math.Max(0, count)),
            EnumerablePartitionerOptions.NoBuffering);
}
```

- [ ] **Step 4: Run the focused tests**

Run the command from Step 2. Expected: all
`VisualComparisonWorkPartitionerTests` pass.

### Task 2: Replace bucket-sized scheduling

**Files:**
- Modify: `VDF.Core/ScanEngine.cs:1422-1453`
- Create: `VDF.Core.Tests/VisualComparisonSchedulingTests.cs`

- [ ] **Step 1: Write the result-equivalence regression test**

Build 5,001 valid in-memory videos so the bucketed path is selected, set
duration tolerance to zero, inject deterministic duplicate pairs, and run
`ScanForDuplicates` with 1 and 4 workers. Normalize opaque group IDs and assert
identical member paths, similarities and flags.

- [ ] **Step 2: Run the regression test against the old scheduler**

Run:

```bash
dotnet test VDF.Core.Tests/VDF.Core.Tests.csproj -c Release \
  --filter FullyQualifiedName~VisualComparisonSchedulingTests
```

Expected: result-equivalence passes; the concurrency assertion from Task 1 is
the failing regression that distinguishes the old whole-bucket design.

- [ ] **Step 3: Schedule every video entry dynamically**

Replace the `smallBuckets` and `largeBuckets` outer loops with:

```csharp
Parallel.ForEach(
    VisualComparisonWorkPartitioner.Create(videoEntries.Count),
    new ParallelOptions {
        CancellationToken = cancelationTokenSource.Token,
        MaxDegreeOfParallelism = VisualComparisonParallelism,
    },
    index => {
        FileEntry entry = videoEntries[index];
        double durationSeconds = entry.mediaInfo!.Duration.TotalSeconds;
        double maxDiffSeconds = GetDurationToleranceSeconds(durationSeconds);
        int minKey = (int)Math.Floor(
            Math.Max(0d, durationSeconds - maxDiffSeconds) / bucketSizeSeconds);
        int maxKey = (int)Math.Floor(
            (durationSeconds + maxDiffSeconds) / bucketSizeSeconds);
        CompareEntry(
            entry,
            entry.compareIndex,
            Enumerable.Range(minKey, maxKey - minKey + 1));
    });
```

- [ ] **Step 4: Run both focused suites**

Run:

```bash
dotnet test VDF.Core.Tests/VDF.Core.Tests.csproj -c Release \
  --filter "FullyQualifiedName~VisualComparisonWorkPartitionerTests|FullyQualifiedName~VisualComparisonSchedulingTests"
```

Expected: all focused tests pass.

### Task 3: Dense-bucket benchmark and combined verification

**Files:**
- Modify: `VDF.Benchmarks/Scenarios/ComparePhaseProbe.cs`

- [ ] **Step 1: Add a dense single-second duration scenario**

Add a pHash scenario with 8,000 videos and a duration spread below one second;
retain the existing 6,000-video dense gray scenario so both comparison modes
remain covered without multiplying the expensive gray-byte workload. Print
duplicate/group counts and peak active workers alongside timing.

- [ ] **Step 2: Run all affected Core tests**

Run:

```bash
dotnet test VDF.Core.Tests/VDF.Core.Tests.csproj -c Release
```

Expected: all tests pass except any pre-existing platform-specific FFT failure,
which must be reported separately rather than hidden.

- [ ] **Step 3: Run the comparison benchmark**

Run:

```bash
dotnet run -c Release --project VDF.Benchmarks -- --probe-compare
```

Expected: duplicate/group counts remain stable; record dense-bucket timings.

- [ ] **Step 4: Build the Web image containing all pending fixes**

Run:

```bash
sudo docker compose build vdf-web
sudo docker compose up -d --force-recreate vdf-web
```

Expected: publish succeeds and the recreated service uses
`vdf-web:compare-optimized`.
