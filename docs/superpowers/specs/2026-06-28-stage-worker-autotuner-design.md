# VDF Stage Worker Autotuner Design

## Goal

Add a read-only hybrid benchmark that finds an independent worker count for every
parallel VDF stage on the target Synology host, writes a portable
`worker-profile.json`, and lets VDF.Web import, export, and manually override the
profile.

The standard preset targets a 45–60 minute run on a host with 24 visible vCPUs.
The benchmark must never modify media files or the production scan database.

## Scope

### Tuned stages

The autotuner measures these stages independently:

1. FFprobe metadata extraction.
2. Video frame hashing.
3. Audio fingerprint hashing.
4. Combined initial scan using the independently selected metadata/frame/audio
   values.
5. Normal grayscale visual comparison.
6. Normal pHash comparison.
7. Partial-clip LSH index construction and candidate search.
8. Partial-clip exact audio verification.
9. Partial visual source sampling.
10. Partial visual clip verification.
11. Result-thumbnail extraction.

Database serialization and result grouping remain single-threaded. They are timed
for reporting but do not receive meaningless worker sweeps.

### Out of scope

- Changing similarity, hashing, sampling, or grouping semantics.
- Automatically running the benchmark from the Web server.
- Dropping Linux filesystem caches.
- Writing benchmark data to the production MemoryPack database.
- Tuning hardware acceleration modes or Native FFmpeg in the first version.

## Architecture

### Benchmark command

Add one direct probe to `VDF.Benchmarks`:

```text
--probe-stage-autotune
  --media-dir /mnt/Videos
  --preset standard
  --output /output/worker-profile.json
  --thumbnail-count 5
```

Presets:

| Preset | Target duration | Maximum real-media sample |
|---|---:|---:|
| quick | 15–20 min | 48 files |
| standard | 45–60 min | 144 files |
| deep | 2–4 h | 512 files |

The worker sweep is capped by `Environment.ProcessorCount` and uses the
interleaved order:

```text
1, max, 2, 20, 4, 16, 6, 12, 8
```

Values above the visible CPU count are omitted; missing useful values such as 24
are inserted from the host CPU count. The normalized set for the target host is:

```text
1, 24, 2, 20, 4, 16, 6, 12, 8
```

### Real-media stages

FFprobe, frame hashing, audio fingerprinting, partial visual sampling, and
thumbnail extraction operate on deterministic read-only samples from
`--media-dir`.

Selection rules:

- Enumerate supported video extensions recursively.
- Sort by normalized path for deterministic selection.
- Stratify by file size into small, medium, and large thirds.
- Select evenly across all three strata.
- Exclude files that disappear or become unreadable before a run.
- Store no media paths in the JSON report; record only aggregate counts.

Every worker value processes the same selected files. Worker order is interleaved
to reduce monotonic temperature and cache bias. Cache dropping is deliberately
avoided because it requires privileged host-wide mutation and could disrupt the
NAS.

The maximum sample is not blindly used for every stage. Before each real-media
sweep, a small one-worker pilot estimates seconds per item. The tuner selects the
largest deterministic prefix predicted to fit that preset's stage budget, with a
minimum of 8 files:

| Stage group | Standard budget |
|---|---:|
| Metadata | 4 min |
| Frame hashing | 10 min |
| Audio fingerprinting | 18 min |
| Partial visual source/clip | 10 min |
| Thumbnail extraction | 8 min |
| Synthetic comparisons and reporting | 10 min |

Quick uses one third of these budgets; deep uses four times these budgets. The
pilot time is included in the stage budget. Once selected, the exact same files
are used for every worker value. A hard budget overrun stops only after the
current file, marks remaining worker values as unmeasured rather than failed, and
does not manufacture a recommendation from incomplete data.

Each run produces a deterministic checksum of successful outputs:

- Metadata: duration, dimensions, codecs, and stream count.
- Frame hash: all returned 32x32 grayscale bytes.
- Audio hash: all fingerprint words.
- Thumbnail: JPEG byte length plus decoded sample checksum.

Different worker counts must produce identical checksums and successful-file
sets. A mismatch marks that run invalid and prevents recommendation.

### Synthetic in-memory stages

Normal comparison, pHash comparison, Partial LSH, and Partial exact verification
reuse deterministic fixed-seed corpora. These stages run three measured
iterations after one warmup and report the median.

The benchmark records duplicate/group counts, candidate-pair counts, injected
match recall, and offsets. A worker value is valid only when its result is
identical to the one-worker reference.

### Combined scan validation

After choosing metadata, frame, and audio values independently, the tuner runs
one read-only combined scan simulation over the real sample. It executes the same
three phase boundaries planned for production:

```text
metadata -> frame hashing -> audio fingerprinting
```

This is a validation run, not another worker sweep. It reports total files/s and
confirms that all per-file output checksums match the individual stages.

## Recommendation algorithm

For each stage:

1. Discard runs with cancellation, fatal errors, result mismatches, or less than
   80% of the reference successful-file count.
2. Find the highest valid throughput.
3. Build the near-best set containing all valid values within 3% of the maximum.
4. Recommend the smallest worker count in the near-best set.

This avoids recommending 24 workers when 8 workers have effectively identical
throughput and lower random-I/O pressure.

The report also includes:

- elapsed time;
- items/s;
- speedup relative to one worker;
- success/failure counts;
- result checksum;
- visible CPU count;
- CLI/Native mode;
- per-stage recommendation and reason.

## Worker profile model

Add a versioned Core model:

```json
{
  "schemaVersion": 1,
  "createdUtc": "2026-06-28T00:00:00Z",
  "visibleCpuCount": 24,
  "preset": "standard",
  "metadataWorkers": 8,
  "frameHashWorkers": 16,
  "audioHashWorkers": 20,
  "visualCompareWorkers": 24,
  "pHashCompareWorkers": 24,
  "partialIndexWorkers": 24,
  "partialExactWorkers": 24,
  "partialVisualSourceWorkers": 6,
  "partialVisualClipWorkers": 6,
  "thumbnailWorkers": 4,
  "stages": []
}
```

`stages` contains the complete measurements used to make each recommendation.
Unknown future fields are ignored. Unsupported schema versions are rejected with
a clear message.

## Runtime settings and compatibility

Add stage-specific integer fields to `Settings`:

- `MetadataMaxDegreeOfParallelism`
- `FrameHashMaxDegreeOfParallelism`
- `AudioHashMaxDegreeOfParallelism`
- `VisualCompareMaxDegreeOfParallelism`
- `PHashCompareMaxDegreeOfParallelism`
- `PartialIndexMaxDegreeOfParallelism`
- `PartialExactMaxDegreeOfParallelism`
- existing `PartialClipVisualMaxDegreeOfParallelism`
- `ThumbnailMaxDegreeOfParallelism`

Value semantics:

- `0`: inherit existing `MaxDegreeOfParallelism`.
- `-1`: use all container-visible CPUs.
- positive value: exact upper limit, capped to a safe maximum of 256.
- other negative values: invalid; inherit global and log a warning.

Old settings files deserialize all new fields as `0`, preserving current
behavior.

Partial source and clip visual stages share the existing
`PartialClipVisualMaxDegreeOfParallelism` because they are consecutive I/O-heavy
parts of one operation. Their benchmark measurements remain separate; profile
generation uses the lower of the two recommendations.

## Scan pipeline scheduling

The initial scan keeps its existing per-file pipeline so one file can advance
from metadata to frame hashing to audio hashing without waiting for the complete
library. Independent `SemaphoreSlim` gates cap each operation type, while the
outer asynchronous pipeline uses the maximum of the three configured limits.
Tasks waiting for a gate do not occupy a worker thread.

Existing cached hashes and fingerprints remain reusable. Pause, cancellation,
failure flags, progress reporting, and periodic database checkpoints remain
supported at file boundaries. This changes scheduling only; per-file decoding
and stored data are unchanged.

## Web UI

Add a `Performance workers` section:

- One numeric input per runtime stage.
- Display `0 = inherit global`, `-1 = all visible CPUs`.
- Show resolved effective worker count beside each value.
- Keep the existing global worker value as fallback.

Add:

- `Import worker profile` using browser file upload.
- `Export worker profile`.
- A profile summary showing creation date, benchmark CPU count, current visible
  CPU count, and warnings if they differ.
- `Apply recommendations` confirmation button.

Import validates the complete file before changing settings. Invalid JSON,
unsupported schema versions, missing recommendations, or values outside the
allowed range leave all existing settings untouched.

Manual edits always override imported values after import.

## Container workflow

Publish `VDF.Benchmarks` into `/app/bench` in the local optimized Web image so the
target host can run:

```bash
sudo docker run --rm \
  --entrypoint dotnet \
  -v /volume7/Datastore1/Videos:/mnt/Videos:ro \
  -v /volume5/docker/vdf/config:/output \
  vdf-web:compare-optimized \
  /app/bench/VDF.Benchmarks.dll \
  --probe-stage-autotune \
  --media-dir /mnt/Videos \
  --preset standard \
  --thumbnail-count 5 \
  --output /output/worker-profile.json
```

The normal Web entrypoint is unchanged.

## Error handling

- Missing media directory or FFmpeg: fail before measurements.
- Fewer files than the preset requests: run with available files and warn.
- Per-file decode errors: record and continue.
- More than 20% failure for a real-media stage: do not recommend that stage.
- Ctrl+C: cancel at file boundaries and write a partial report marked
  `completed=false`; partial recommendations are not importable.
- Output write failure: print the full report to stdout and return nonzero.

## Testing

### Unit tests

- Worker sweep generation for 1, 4, 20, and 24 CPUs.
- 3%-near-best recommendation chooses the lowest worker.
- Invalid and mismatching runs are excluded.
- Profile JSON round-trip and schema rejection.
- Settings inheritance and `-1` resolution.
- Import is atomic on invalid input.
- Old Web settings retain global inheritance.

### Integration tests

- Deterministic media sampling.
- FFprobe/frame/audio checksums are identical across 1 and 2 workers.
- Benchmark never writes below the media directory.
- Profile import applies every stage field.
- Combined scan produces the same hashes/fingerprints as the old per-file
  sequence on fixed videos.

### Target-host acceptance

- Standard preset finishes in approximately 45–60 minutes.
- All 24-vCPU worker values are evaluated where meaningful.
- Every stage prints a recommendation or a precise reason why none is safe.
- Imported profile changes resolved Web values.
- A production rescan loads the existing database and does not rehash files whose
  data is already complete.
