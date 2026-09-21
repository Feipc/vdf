// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

namespace VDF.Core.Tests;

public class PartialClipFingerprintIndexTests {
	[Fact]
	public void Settings_DefaultToFastBalancedProfile() {
		var settings = new Settings();

		Assert.Equal(PartialClipSearchMode.FastBalanced, settings.PartialClipSearchMode);
		Assert.Equal(256, settings.PartialClipMaxCandidates);
		Assert.Equal(8192, settings.PartialClipIndexMemoryLimitMB);
	}

	[Fact]
	public void Settings_OldJsonKeepsFastDefaultsAndStringModeLoads() {
		Settings oldSettings = System.Text.Json.JsonSerializer.Deserialize(
			"""{"EnablePartialClipDetection":true}""",
			VDF.Core.Utils.CoreJsonContext.Default.Settings)!;
		Settings exactSettings = System.Text.Json.JsonSerializer.Deserialize(
			"""{"PartialClipSearchMode":"Exact"}""",
			VDF.Core.Utils.CoreJsonContext.Default.Settings)!;

		Assert.Equal(PartialClipSearchMode.FastBalanced, oldSettings.PartialClipSearchMode);
		Assert.Equal(256, oldSettings.PartialClipMaxCandidates);
		Assert.Equal(8192, oldSettings.PartialClipIndexMemoryLimitMB);
		Assert.Equal(PartialClipSearchMode.Exact, exactSettings.PartialClipSearchMode);
	}

	[Fact]
	public void FindCandidates_EmbeddedNoisyClipFindsSourceDeterministically() {
		uint[] source = RandomFingerprint(240, seed: 1234);
		uint[] clip = source[60..180].ToArray();
		for (int i = 0; i < clip.Length; i += 5)
			clip[i] ^= 1u << (i % 32);

		var entries = new[] {
			Entry(240, source, 0),
			Entry(120, clip, 1),
		};

		Assert.True(PartialClipFingerprintIndex.TryBuild(
			entries,
			memoryLimitBytes: 256 * 1024 * 1024,
			CancellationToken.None,
			out PartialClipFingerprintIndex? index,
			out _));
		Assert.NotNull(index);

		PartialClipCandidateResult first = index.FindCandidates(
			clipIndex: 1,
			entries,
			minRatio: 0.10,
			maxCandidates: 256);
		PartialClipCandidateResult second = index.FindCandidates(
			clipIndex: 1,
			entries,
			minRatio: 0.10,
			maxCandidates: 256);

		Assert.False(first.RequiresExactFallback);
		Assert.Contains(0, first.SourceIndices);
		Assert.Equal(first.SourceIndices, second.SourceIndices);
	}

	[Fact]
	public void FindCandidates_ShortClipRequestsExactFallback() {
		var entries = new[] {
			Entry(180, RandomFingerprint(180, 1), 0),
			Entry(89, RandomFingerprint(89, 2), 1),
		};
		Assert.True(PartialClipFingerprintIndex.TryBuild(
			entries,
			256 * 1024 * 1024,
			CancellationToken.None,
			out PartialClipFingerprintIndex? index,
			out _));

		PartialClipCandidateResult result = index!.FindCandidates(1, entries, 0.10, 256);

		Assert.True(result.RequiresExactFallback);
		Assert.Empty(result.SourceIndices);
	}

	[Fact]
	public void TryBuild_InsufficientMemoryReturnsFalseWithoutAllocatingIndex() {
		var entries = new[] {
			Entry(1000, RandomFingerprint(1000, 1), 0),
			Entry(500, RandomFingerprint(500, 2), 1),
		};

		bool built = PartialClipFingerprintIndex.TryBuild(
			entries,
			memoryLimitBytes: 1024,
			CancellationToken.None,
			out PartialClipFingerprintIndex? index,
			out PartialClipIndexBuildStats stats);

		Assert.False(built);
		Assert.Null(index);
		Assert.True(stats.EstimatedBytes > 1024);
	}

	[Fact]
	public void FindCandidates_RespectsMaximumCandidateCount() {
		uint[] clip = RandomFingerprint(120, 99);
		var entries = Enumerable.Range(0, 20)
			.Select(i => Entry(240 - i, Embed(clip, 240 - i, i % 30), i))
			.Append(Entry(120, clip, 20))
			.ToArray();

		Assert.True(PartialClipFingerprintIndex.TryBuild(
			entries,
			512 * 1024 * 1024,
			CancellationToken.None,
			out PartialClipFingerprintIndex? index,
			out _));

		PartialClipCandidateResult result = index!.FindCandidates(20, entries, 0.10, maxCandidates: 5);

		Assert.False(result.RequiresExactFallback);
		Assert.Equal(5, result.SourceIndices.Length);
		Assert.Equal(5, result.SourceIndices.Distinct().Count());
	}

	[Fact]
	public void FindCandidates_AtEightyPercentBitSimilarityStillFindsInjectedSource() {
		uint[] source = RandomFingerprint(600, 700);
		uint[] clip = source[100..356].ToArray();
		FlipBits(clip, probability: 0.20, seed: 701);
		var entries = new[] {
			Entry(600, source, 0),
			Entry(256, clip, 1),
		};

		Assert.True(PartialClipFingerprintIndex.TryBuild(
			entries,
			256 * 1024 * 1024,
			CancellationToken.None,
			out PartialClipFingerprintIndex? index,
			out _));

		PartialClipCandidateResult result = index!.FindCandidates(1, entries, 0.10, 256);

		Assert.False(result.RequiresExactFallback);
		Assert.Contains(0, result.SourceIndices);
	}

	[Fact]
	public void FindCandidates_AllHotBucketsRequestExactFallback() {
		var entries = Enumerable.Range(0, PartialClipFingerprintIndex.HotBucketLimit + 2)
			.Select(i => Entry(240, new uint[120], i))
			.ToArray();

		Assert.True(PartialClipFingerprintIndex.TryBuild(
			entries,
			256 * 1024 * 1024,
			CancellationToken.None,
			out PartialClipFingerprintIndex? index,
			out PartialClipIndexBuildStats stats));

		PartialClipCandidateResult result = index!.FindCandidates(0, entries, 0.10, 256);

		Assert.True(stats.HotBucketCount > 0);
		Assert.True(result.RequiresExactFallback);
		Assert.Equal(0, result.UsableAnchors);
	}

	[Fact]
	public void IndexedPlan_IsDeterministicAcrossWorkersAndCutsCandidatePairsByNinetyNinePercent() {
		uint[] clip = RandomFingerprint(120, 800);
		var entries = Enumerable.Range(0, 100)
			.Select(i => Entry(
				300,
				i == 0 ? Embed(clip, 300, 75) : RandomFingerprint(300, 900 + i),
				i))
			.Append(Entry(120, clip, 100))
			.ToArray();
		PartialComparisonPlan exact = PartialComparisonPlanner.Create(entries, 0.10);

		Assert.True(PartialClipFingerprintIndex.TryBuild(
			entries,
			256 * 1024 * 1024,
			CancellationToken.None,
			out PartialClipFingerprintIndex? index,
			out _));
		PartialIndexedComparisonPlan oneWorker = PartialIndexedComparisonPlanner.Create(
			entries, index!, 0.10, 256, 1, CancellationToken.None);
		PartialIndexedComparisonPlan fourWorkers = PartialIndexedComparisonPlanner.Create(
			entries, index!, 0.10, 256, 4, CancellationToken.None);

		Assert.Equal(100L, exact.CandidatePairCount);
		Assert.True(oneWorker.CandidatePairCount <= exact.CandidatePairCount / 100);
		Assert.Equal(Flatten(oneWorker), Flatten(fourWorkers));
		Assert.Contains((100, 0), Flatten(oneWorker));
	}

	static PartialCompareEntry Entry(double duration, uint[] fingerprint, int index) =>
		new(new FileEntry(), duration, fingerprint, index);

	static uint[] RandomFingerprint(int length, int seed) {
		var rng = new Random(seed);
		var result = new uint[length];
		for (int i = 0; i < result.Length; i++)
			result[i] = (uint)rng.NextInt64(1, uint.MaxValue);
		return result;
	}

	static uint[] Embed(uint[] clip, int sourceLength, int offset) {
		uint[] source = RandomFingerprint(sourceLength, sourceLength * 17 + offset);
		Array.Copy(clip, 0, source, offset, clip.Length);
		return source;
	}

	static void FlipBits(uint[] fingerprint, double probability, int seed) {
		var rng = new Random(seed);
		for (int i = 0; i < fingerprint.Length; i++) {
			uint mask = 0;
			for (int bit = 0; bit < 32; bit++)
				if (rng.NextDouble() < probability)
					mask |= 1u << bit;
			fingerprint[i] ^= mask;
		}
	}

	static (int Clip, int Source)[] Flatten(PartialIndexedComparisonPlan plan) =>
		plan.Batches
			.SelectMany(batch => Enumerable
				.Range(batch.Start, batch.End - batch.Start)
				.Select(position => (batch.PrimaryIndex, batch.SourceIndices![position])))
			.ToArray();
}
