// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

namespace VDF.Core.Tests;

public class PartialComparisonPlannerTests {
	[Theory]
	[InlineData(-1, 20, 20)]
	[InlineData(1, 20, 1)]
	[InlineData(8, 20, 8)]
	[InlineData(0, 20, 1)]
	[InlineData(-2, 20, 1)]
	public void ResolveParallelism_NormalizesConfiguredValue(int configured, int processorCount, int expected) {
		Assert.Equal(expected, ParallelismUtils.Resolve(configured, processorCount));
	}

	[Fact]
	public void Create_UsesExactDurationRatioBoundaries() {
		var entries = new[] {
			Entry(100.0),
			Entry(95.0),
			Entry(94.999),
			Entry(10.0),
			Entry(9.999),
		};

		PartialComparisonPlan plan = PartialComparisonPlanner.Create(entries, minRatio: 0.10, batchSize: 64);
		var sourceZeroPairs = plan.Batches
			.Where(b => b.SourceIndex == 0)
			.SelectMany(b => Enumerable.Range(b.StartClipIndex, b.EndClipIndex - b.StartClipIndex))
			.ToArray();

		Assert.Equal(new[] { 2, 3 }, sourceZeroPairs);
	}

	[Fact]
	public void Create_SplitsLargeRowsIntoAtMost64Pairs() {
		var entries = new[] { Entry(100.0) }
			.Concat(Enumerable.Range(0, 130).Select(i => Entry(94.9 - i * 0.01)))
			.ToArray();

		PartialComparisonPlan plan = PartialComparisonPlanner.Create(entries, minRatio: 0.0, batchSize: 64);
		var sourceZeroBatches = plan.Batches.Where(b => b.SourceIndex == 0).ToArray();

		Assert.Equal(3, sourceZeroBatches.Length);
		Assert.All(sourceZeroBatches, b => Assert.InRange(b.EndClipIndex - b.StartClipIndex, 1, 64));
		Assert.Equal(130, sourceZeroBatches.Sum(b => b.EndClipIndex - b.StartClipIndex));
	}

	[Fact]
	public void CountCandidates_Uses64BitArithmetic() {
		var rows = new[] {
			new PartialCandidateRow(0, 0, int.MaxValue),
			new PartialCandidateRow(1, 0, 10),
		};

		Assert.Equal((long)int.MaxValue + 10, PartialComparisonPlanner.CountCandidates(rows));
	}

	[Fact]
	public void Create_MatchesReferenceNestedLoopForRandomDurations() {
		const double minRatio = 0.10;
		var rng = new Random(12345);
		var entries = Enumerable.Range(0, 500)
			.Select(_ => Entry(1 + rng.NextDouble() * 7200))
			.OrderByDescending(e => e.DurationSeconds)
			.ToArray();

		var expected = new List<(int Source, int Clip)>();
		for (int source = 0; source < entries.Length - 1; source++) {
			for (int clip = source + 1; clip < entries.Length; clip++) {
				double ratio = entries[clip].DurationSeconds / entries[source].DurationSeconds;
				if (ratio >= minRatio && ratio < 0.95)
					expected.Add((source, clip));
			}
		}

		PartialComparisonPlan plan = PartialComparisonPlanner.Create(entries, minRatio, batchSize: 17);
		var actual = plan.Batches
			.SelectMany(batch => Enumerable
				.Range(batch.StartClipIndex, batch.EndClipIndex - batch.StartClipIndex)
				.Select(clip => (batch.SourceIndex, clip)))
			.ToArray();
		double expectedWork = expected.Sum(pair =>
			PartialComparisonPlanner.EstimateWork(entries[pair.Source], entries[pair.Clip]));

		Assert.Equal(expected, actual);
		Assert.Equal(expected.Count, plan.CandidatePairCount);
		Assert.InRange(
			Math.Abs(expectedWork - plan.EstimatedWork),
			0,
			Math.Max(1, expectedWork) * 1e-10);
	}

	static PartialCompareEntry Entry(double durationSeconds) =>
		new(new FileEntry(), durationSeconds, new uint[] { 1, 2 }, 0);
}
