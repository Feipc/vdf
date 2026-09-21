// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System;
using System.Collections.Generic;

namespace VDF.Core {
	internal static class ParallelismUtils {
		internal static int Resolve(int configured) =>
			Resolve(configured, Environment.ProcessorCount);

		internal static int Resolve(int configured, int processorCount) {
			if (configured == -1)
				return Math.Max(1, processorCount);
			return configured > 0 ? configured : 1;
		}
	}

	internal readonly record struct PartialCompareEntry(
		FileEntry Entry,
		double DurationSeconds,
		uint[] Fingerprint,
		int OriginalIndex);

	internal readonly record struct PartialCandidateRow(
		int SourceIndex,
		int StartClipIndex,
		int EndClipIndex,
		double EstimatedWork = 0);

	internal readonly record struct PartialCompareBatch(
		int SourceIndex,
		int StartClipIndex,
		int EndClipIndex);

	internal readonly record struct PartialComparisonWorkBatch(
		int PrimaryIndex,
		int Start,
		int End,
		int[]? SourceIndices);

	internal sealed class PartialComparisonPlan {
		readonly IReadOnlyList<PartialCandidateRow> rows;
		readonly int batchSize;

		internal PartialComparisonPlan(
			IReadOnlyList<PartialCompareEntry> entries,
			IReadOnlyList<PartialCandidateRow> rows,
			int batchSize) {
			Entries = entries;
			this.rows = rows;
			this.batchSize = batchSize;
			CandidatePairCount = PartialComparisonPlanner.CountCandidates(rows);
			EstimatedWork = PartialComparisonPlanner.CountEstimatedWork(rows);
		}

		internal IReadOnlyList<PartialCompareEntry> Entries { get; }
		internal long CandidatePairCount { get; }
		internal double EstimatedWork { get; }
		internal IEnumerable<PartialCompareBatch> Batches => EnumerateBatches();

		IEnumerable<PartialCompareBatch> EnumerateBatches() {
			foreach (PartialCandidateRow row in rows) {
				for (int start = row.StartClipIndex; start < row.EndClipIndex; start += batchSize) {
					yield return new PartialCompareBatch(
						row.SourceIndex,
						start,
						Math.Min(row.EndClipIndex, start + batchSize));
				}
			}
		}
	}

	internal sealed class PartialIndexedComparisonPlan {
		readonly int[][] sourcesByClip;
		readonly int batchSize;

		internal PartialIndexedComparisonPlan(
			int[][] sourcesByClip,
			int batchSize,
			long candidatePairCount,
			double estimatedWork,
			int fallbackClipCount,
			long postingVisits) {
			this.sourcesByClip = sourcesByClip;
			this.batchSize = batchSize;
			CandidatePairCount = candidatePairCount;
			EstimatedWork = estimatedWork;
			FallbackClipCount = fallbackClipCount;
			PostingVisits = postingVisits;
		}

		internal long CandidatePairCount { get; }
		internal double EstimatedWork { get; }
		internal int FallbackClipCount { get; }
		internal long PostingVisits { get; }
		internal IEnumerable<PartialComparisonWorkBatch> Batches => EnumerateBatches();

		IEnumerable<PartialComparisonWorkBatch> EnumerateBatches() {
			for (int clipIndex = 0; clipIndex < sourcesByClip.Length; clipIndex++) {
				int[] sources = sourcesByClip[clipIndex];
				for (int start = 0; start < sources.Length; start += batchSize) {
					yield return new PartialComparisonWorkBatch(
						clipIndex,
						start,
						Math.Min(sources.Length, start + batchSize),
						sources);
				}
			}
		}
	}

	internal static class PartialIndexedComparisonPlanner {
		internal static PartialIndexedComparisonPlan Create(
			IReadOnlyList<PartialCompareEntry> entries,
			PartialClipFingerprintIndex index,
			double minRatio,
			int maxCandidates,
			int maxDegreeOfParallelism,
			CancellationToken cancellationToken,
			Action? itemCompleted = null,
			Action<int>? activeWorkerDelta = null,
			int batchSize = PartialComparisonPlanner.DefaultBatchSize) {
			var sourcesByClip = new int[entries.Count][];
			long candidatePairCount = 0;
			double estimatedWork = 0;
			int fallbackClipCount = 0;
			long postingVisits = 0;

			Parallel.For(
				0,
				entries.Count,
				new ParallelOptions {
					CancellationToken = cancellationToken,
					MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism),
				},
				clipIndex => {
					activeWorkerDelta?.Invoke(1);
					try {
						PartialClipCandidateResult result = index.FindCandidates(
							clipIndex,
							entries,
							minRatio,
							Math.Max(1, maxCandidates));
						int[] sources = result.RequiresExactFallback
							? GetExactSources(clipIndex, entries, minRatio)
							: result.SourceIndices;
						sourcesByClip[clipIndex] = sources;

						double localWork = 0;
						foreach (int sourceIndex in sources)
							localWork += PartialComparisonPlanner.EstimateWork(entries[sourceIndex], entries[clipIndex]);
						Interlocked.Add(ref candidatePairCount, sources.Length);
						AtomicAdd(ref estimatedWork, localWork);
						Interlocked.Add(ref postingVisits, result.PostingVisits);
						if (result.RequiresExactFallback)
							Interlocked.Increment(ref fallbackClipCount);
						itemCompleted?.Invoke();
					}
					finally {
						activeWorkerDelta?.Invoke(-1);
					}
				});

			for (int i = 0; i < sourcesByClip.Length; i++)
				sourcesByClip[i] ??= Array.Empty<int>();
			return new PartialIndexedComparisonPlan(
				sourcesByClip,
				batchSize,
				candidatePairCount,
				estimatedWork,
				fallbackClipCount,
				postingVisits);
		}

		static int[] GetExactSources(
			int clipIndex,
			IReadOnlyList<PartialCompareEntry> entries,
			double minRatio) {
			if (double.IsNaN(minRatio) || minRatio >= 0.95)
				return Array.Empty<int>();
			PartialCompareEntry clip = entries[clipIndex];
			var sources = new List<int>();
			for (int sourceIndex = 0; sourceIndex < entries.Count; sourceIndex++) {
				if (sourceIndex == clipIndex)
					continue;
				PartialCompareEntry source = entries[sourceIndex];
				double ratio = clip.DurationSeconds / source.DurationSeconds;
				if (ratio < minRatio || ratio >= 0.95)
					continue;
				if (clip.Fingerprint.Length >= source.Fingerprint.Length)
					continue;
				sources.Add(sourceIndex);
			}
			return sources.ToArray();
		}

		static void AtomicAdd(ref double location, double value) {
			double current;
			do {
				current = Volatile.Read(ref location);
			}
			while (Interlocked.CompareExchange(ref location, current + value, current) != current);
		}
	}

	internal static class PartialComparisonPlanner {
		internal const int DefaultBatchSize = 64;

		internal static PartialComparisonPlan Create(
			IReadOnlyList<PartialCompareEntry> entries,
			double minRatio,
			int batchSize = DefaultBatchSize) {
			if (batchSize <= 0)
				throw new ArgumentOutOfRangeException(nameof(batchSize));

			var rows = new List<PartialCandidateRow>(Math.Max(0, entries.Count - 1));
			var durationPrefix = new double[entries.Count + 1];
			var durationSquaredPrefix = new double[entries.Count + 1];
			for (int i = 0; i < entries.Count; i++) {
				double duration = entries[i].DurationSeconds;
				durationPrefix[i + 1] = durationPrefix[i] + duration;
				durationSquaredPrefix[i + 1] = durationSquaredPrefix[i] + duration * duration;
			}
			if (double.IsNaN(minRatio) || minRatio < 0.95) {
				for (int sourceIndex = 0; sourceIndex < entries.Count - 1; sourceIndex++) {
					double sourceSeconds = entries[sourceIndex].DurationSeconds;
					int firstPossibleClip = sourceIndex + 1;
					int start = FirstRatioLessThan(
						entries,
						sourceSeconds,
						0.95,
						firstPossibleClip);
					int end = minRatio <= 0
						? entries.Count
						: FirstRatioLessThan(
							entries,
							sourceSeconds,
							minRatio,
							start);

					if (start < end) {
						double durationSum = durationPrefix[end] - durationPrefix[start];
						double durationSquaredSum = durationSquaredPrefix[end] - durationSquaredPrefix[start];
						double estimatedWork = (sourceSeconds + 1) * durationSum - durationSquaredSum;
						rows.Add(new PartialCandidateRow(sourceIndex, start, end, Math.Max(0, estimatedWork)));
					}
				}
			}

			return new PartialComparisonPlan(entries, rows, batchSize);
		}

		internal static long CountCandidates(IEnumerable<PartialCandidateRow> rows) {
			long count = 0;
			foreach (PartialCandidateRow row in rows)
				count += (long)row.EndClipIndex - row.StartClipIndex;
			return count;
		}

		internal static double CountEstimatedWork(IEnumerable<PartialCandidateRow> rows) {
			double count = 0;
			foreach (PartialCandidateRow row in rows)
				count += row.EstimatedWork;
			return count;
		}

		internal static double EstimateWork(PartialCompareEntry source, PartialCompareEntry clip) =>
			Math.Max(0, source.DurationSeconds - clip.DurationSeconds + 1) * clip.DurationSeconds;

		static int FirstRatioLessThan(
			IReadOnlyList<PartialCompareEntry> entries,
			double sourceSeconds,
			double ratio,
			int startIndex) {
			int low = startIndex;
			int high = entries.Count;
			while (low < high) {
				int mid = low + ((high - low) >> 1);
				if (entries[mid].DurationSeconds / sourceSeconds < ratio)
					high = mid;
				else
					low = mid + 1;
			}
			return low;
		}
	}
}
