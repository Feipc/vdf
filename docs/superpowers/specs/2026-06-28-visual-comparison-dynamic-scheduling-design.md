# Visual Comparison Dynamic Scheduling

## Problem

The large-library visual comparison path schedules small duration buckets as
the unit of parallel work, then processes every video in a bucket serially.
When one dense bucket remains, the UI can report `Workers: 1 / 24` while
thousands of videos remain. Those entries are independent but invisible to the
parallel scheduler.

## Design

For datasets using the duration-bucket comparison path, schedule every video
entry as an independently stealable work item through a no-buffering dynamic
partitioner. Each item continues to:

- derive the same duration-tolerance bucket range;
- call the existing `CompareEntry` implementation;
- compare against the same candidate entries;
- apply the same pHash/gray comparison, thresholds, flipped-image option,
  hard-link checks and grouping logic;
- report one completed progress item.

Duration buckets remain the candidate index. Only the outer scheduling unit
changes from one whole bucket to one video entry. No pair list is materialized.
Images and the existing small-dataset linear path are unchanged.

## Determinism and Accuracy

The change must not add or remove any legal comparison pair. Tests compare:

- the planned candidate pair set before and after flattening;
- duplicate paths, similarity and group membership at 1 and 24 workers;
- repeated 24-worker runs for stable results.

Existing grouping synchronization remains in place. If testing exposes
order-dependent group identifiers, assertions normalize opaque group IDs while
requiring identical group membership.

## Performance Validation

Add a benchmark corpus dominated by one duration bucket. Record elapsed time,
throughput and peak active workers at 1, 4, 12 and 24 workers. The 24-worker run
must expose more than one active worker while at least 24 entries remain. CI
will not enforce a wall-clock speed threshold.

## Scope

This change does not modify hash generation, candidate filtering, similarity
math, Partial Clip detection, database format or Web settings. It is deployed
with the pending ETA, atomic progress and FFmpeg timeout fixes and verified in
the same Synology build.
