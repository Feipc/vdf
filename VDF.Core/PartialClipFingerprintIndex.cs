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
using System.Linq;
using System.Runtime.CompilerServices;

namespace VDF.Core {
	internal readonly record struct PartialClipIndexBuildStats(
		long FingerprintBlocks,
		long PostingCount,
		long EstimatedBytes,
		int HotBucketCount);

	internal readonly record struct PartialClipCandidateResult(
		int[] SourceIndices,
		bool RequiresExactFallback,
		int UsableAnchors,
		int RequiredVotes,
		long PostingVisits);

	/// <summary>
	/// Transient multi-table locality-sensitive index over one-second audio fingerprints.
	/// Each table stores a compact CSR posting list keyed by 20 selected bits from two
	/// fingerprint blocks. Candidate hits are still verified by the exact sliding matcher.
	/// </summary>
	internal sealed class PartialClipFingerprintIndex {
		internal const int TableCount = 8;
		internal const int KeyBits = 20;
		internal const int KeySpace = 1 << KeyBits;
		internal const int HotBucketLimit = 2048;
		internal const int MinIndexedClipLength = 90;
		internal const int MinUsableAnchors = 16;
		internal const int MaxAnchors = 256;
		const int OffsetBucketSeconds = 3;
		const int MaxVoteKeys = 500_000;

		static readonly int[] CurrentShifts = { 0, 4, 8, 12, 16, 20, 2, 10 };
		static readonly int[] FutureShifts = { 10, 14, 18, 22, 0, 6, 16, 4 };
		static readonly int[] Lags = { 1, 2, 3, 5, 1, 2, 3, 5 };

		readonly int[][] starts;
		readonly ulong[][] postings;

		PartialClipFingerprintIndex(int[][] starts, ulong[][] postings) {
			this.starts = starts;
			this.postings = postings;
		}

		internal static bool TryBuild(
			IReadOnlyList<PartialCompareEntry> entries,
			long memoryLimitBytes,
			CancellationToken cancellationToken,
			out PartialClipFingerprintIndex? index,
			out PartialClipIndexBuildStats stats,
			Action? tableCompleted = null,
			Action<int>? activeWorkerDelta = null,
			int maxDegreeOfParallelism = -1) {
			index = null;
			long fingerprintBlocks = 0;
			long postingCount = 0;
			var postingsPerTable = new long[TableCount];
			foreach (PartialCompareEntry entry in entries)
				fingerprintBlocks += entry.Fingerprint.Length;
			for (int table = 0; table < TableCount; table++) {
				int lag = Lags[table];
				long tablePostings = 0;
				foreach (PartialCompareEntry entry in entries)
					tablePostings += Math.Max(0, entry.Fingerprint.Length - lag);
				postingsPerTable[table] = tablePostings;
				postingCount += tablePostings;
			}

			// Tables are built in parallel, so budget for every temporary count array,
			// not just one.  This deliberately over-estimates by a few integers and keeps
			// the configured cap a hard upper bound during index construction.
			long startsBytes = (long)TableCount * (KeySpace + 1) * sizeof(int);
			long temporaryCountBytes = (long)TableCount * KeySpace * sizeof(int);
			long estimatedBytes = startsBytes + temporaryCountBytes + postingCount * sizeof(ulong);
			stats = new PartialClipIndexBuildStats(
				fingerprintBlocks,
				postingCount,
				estimatedBytes,
				0);
			if (memoryLimitBytes <= 0 || estimatedBytes > memoryLimitBytes)
				return false;
			if (postingsPerTable.Any(x => x > int.MaxValue))
				return false;

			var builtStarts = new int[TableCount][];
			var builtPostings = new ulong[TableCount][];
			try {
				Parallel.For(
					0,
					TableCount,
					new ParallelOptions {
						CancellationToken = cancellationToken,
						MaxDegreeOfParallelism = Math.Min(
							TableCount,
							maxDegreeOfParallelism < 1
								? Math.Max(1, Environment.ProcessorCount)
								: maxDegreeOfParallelism),
					},
					table => {
						activeWorkerDelta?.Invoke(1);
						try {
							BuildTable(entries, table, builtStarts, builtPostings, cancellationToken);
							tableCompleted?.Invoke();
						}
						finally {
							activeWorkerDelta?.Invoke(-1);
						}
					});
			}
			catch (OutOfMemoryException) {
				return false;
			}
			catch (AggregateException ex) when (ex.InnerExceptions.Any(x => x is OutOfMemoryException)) {
				return false;
			}

			int hotBuckets = 0;
			for (int table = 0; table < TableCount; table++) {
				int[] tableStarts = builtStarts[table];
				for (int key = 0; key < KeySpace; key++)
					if (tableStarts[key + 1] - tableStarts[key] > HotBucketLimit)
						hotBuckets++;
			}

			index = new PartialClipFingerprintIndex(builtStarts, builtPostings);
			stats = stats with { HotBucketCount = hotBuckets };
			return true;
		}

		static void BuildTable(
			IReadOnlyList<PartialCompareEntry> entries,
			int table,
			int[][] builtStarts,
			ulong[][] builtPostings,
			CancellationToken cancellationToken) {
			var counts = new int[KeySpace];
			int lag = Lags[table];
			for (int videoId = 0; videoId < entries.Count; videoId++) {
				uint[] fingerprint = entries[videoId].Fingerprint;
				for (int position = 0; position + lag < fingerprint.Length; position++) {
					if ((position & 0x3fff) == 0)
						cancellationToken.ThrowIfCancellationRequested();
					counts[GetKey(table, fingerprint, position)]++;
				}
			}

			var tableStarts = new int[KeySpace + 1];
			int total = 0;
			for (int key = 0; key < KeySpace; key++) {
				tableStarts[key] = total;
				total += counts[key];
				counts[key] = tableStarts[key];
			}
			tableStarts[KeySpace] = total;

			var tablePostings = new ulong[total];
			for (int videoId = 0; videoId < entries.Count; videoId++) {
				uint[] fingerprint = entries[videoId].Fingerprint;
				for (int position = 0; position + lag < fingerprint.Length; position++) {
					if ((position & 0x3fff) == 0)
						cancellationToken.ThrowIfCancellationRequested();
					int key = GetKey(table, fingerprint, position);
					tablePostings[counts[key]++] = PackPosting(videoId, position);
				}
			}
			builtStarts[table] = tableStarts;
			builtPostings[table] = tablePostings;
		}

		internal PartialClipCandidateResult FindCandidates(
			int clipIndex,
			IReadOnlyList<PartialCompareEntry> entries,
			double minRatio,
			int maxCandidates) {
			PartialCompareEntry clip = entries[clipIndex];
			if (double.IsNaN(minRatio) || minRatio >= 0.95)
				return new PartialClipCandidateResult(Array.Empty<int>(), false, 0, 0, 0);
			if (clip.Fingerprint.Length < MinIndexedClipLength || maxCandidates <= 0)
				return FallbackResult();

			int availablePositions = Math.Max(1, clip.Fingerprint.Length - Lags.Max());
			int stride = Math.Max(1, (availablePositions + MaxAnchors - 1) / MaxAnchors);
			var votes = new Dictionary<ulong, VoteState>();
			int usableAnchors = 0;
			int anchorOrdinal = 0;
			long postingVisits = 0;

			for (int clipPosition = 0; clipPosition < availablePositions; clipPosition += stride) {
				bool usable = false;
				for (int table = 0; table < TableCount; table++) {
					if (clipPosition + Lags[table] >= clip.Fingerprint.Length)
						continue;
					int key = GetKey(table, clip.Fingerprint, clipPosition);
					int start = starts[table][key];
					int end = starts[table][key + 1];
					int bucketLength = end - start;
					if (bucketLength == 0 || bucketLength > HotBucketLimit)
						continue;
					usable = true;
					postingVisits += bucketLength;

					ulong[] tablePostings = postings[table];
					for (int p = start; p < end; p++) {
						UnpackPosting(tablePostings[p], out int sourceIndex, out int sourcePosition);
						if (sourceIndex == clipIndex)
							continue;
						PartialCompareEntry source = entries[sourceIndex];
						double ratio = clip.DurationSeconds / source.DurationSeconds;
						if (ratio < minRatio || ratio >= 0.95)
							continue;
						if (clip.Fingerprint.Length >= source.Fingerprint.Length)
							continue;

						int offset = sourcePosition - clipPosition;
						if (offset < 0 || offset + clip.Fingerprint.Length > source.Fingerprint.Length)
							continue;
						int offsetBucket = offset / OffsetBucketSeconds;
						ulong voteKey = ((ulong)(uint)sourceIndex << 32) | (uint)offsetBucket;
						if (votes.TryGetValue(voteKey, out VoteState state)) {
							if (state.LastAnchor != anchorOrdinal) {
								votes[voteKey] = new VoteState(state.Count + 1, anchorOrdinal);
							}
						}
						else {
							if (votes.Count >= MaxVoteKeys)
								return FallbackResult(usableAnchors, postingVisits);
							votes.Add(voteKey, new VoteState(1, anchorOrdinal));
						}
					}
				}
				if (usable)
					usableAnchors++;
				anchorOrdinal++;
			}

			if (usableAnchors < MinUsableAnchors)
				return FallbackResult(usableAnchors, postingVisits);

			int requiredVotes = Math.Max(3, Math.Min(12, (int)Math.Ceiling(usableAnchors * 0.03)));
			var bestBySource = new Dictionary<int, int>();
			foreach ((ulong key, VoteState state) in votes) {
				if (state.Count < requiredVotes)
					continue;
				int sourceIndex = (int)(key >> 32);
				if (!bestBySource.TryGetValue(sourceIndex, out int best) || state.Count > best)
					bestBySource[sourceIndex] = state.Count;
			}

			int[] candidates = bestBySource
				.OrderByDescending(x => x.Value)
				.ThenBy(x => x.Key)
				.Take(maxCandidates)
				.Select(x => x.Key)
				.ToArray();
			return new PartialClipCandidateResult(
				candidates,
				false,
				usableAnchors,
				requiredVotes,
				postingVisits);
		}

		static PartialClipCandidateResult FallbackResult(int usableAnchors = 0, long postingVisits = 0) =>
			new(Array.Empty<int>(), true, usableAnchors, 0, postingVisits);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static int GetKey(int table, uint[] fingerprint, int position) {
			uint current = fingerprint[position];
			uint future = fingerprint[position + Lags[table]];
			return (int)(((current >> CurrentShifts[table]) & 0x3ffu)
				| (((future >> FutureShifts[table]) & 0x3ffu) << 10));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static ulong PackPosting(int videoId, int position) =>
			((ulong)(uint)videoId << 32) | (uint)position;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void UnpackPosting(ulong posting, out int videoId, out int position) {
			videoId = (int)(posting >> 32);
			position = (int)posting;
		}

		readonly record struct VoteState(int Count, int LastAnchor);
	}
}
