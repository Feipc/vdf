// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

namespace VDF.Core.Tests;

public class PartialVisualVerificationTests {
	[Theory]
	[InlineData(20, 6, 6)]
	[InlineData(4, 6, 4)]
	[InlineData(20, 8, 8)]
	[InlineData(20, 0, 6)]
	[InlineData(20, -1, 6)]
	public void ResolveParallelism_CapsVisualWorkers(
		int totalWorkers,
		int visualLimit,
		int expected) {
		Assert.Equal(
			expected,
			PartialVisualVerificationUtils.ResolveParallelism(totalWorkers, visualLimit));
	}

	[Fact]
	public void BuildClipTimes_PreservesExistingThreeFramePositions() {
		Assert.Equal(
			new[] { 25d, 50d, 75d },
			PartialVisualVerificationUtils.BuildClipTimes(100));
		Assert.Equal(
			new[] { 1.98d, 3.96d },
			PartialVisualVerificationUtils.BuildClipTimes(6));
		Assert.Equal(
			new[] { 1d },
			PartialVisualVerificationUtils.BuildClipTimes(2));
	}

	[Theory]
	[InlineData(1, 1, 1)]
	[InlineData(2, 1, 2)]
	[InlineData(3, 1, 3)]
	[InlineData(24, 1, 24)]
	[InlineData(25, 2, 1)]
	public void CliBatches_AreCappedAtTwentyFourFrames(
		int count,
		int expectedBatchCount,
		int expectedLastBatchCount) {
		var batches = PartialVisualVerificationUtils.GetBatches(count, 24);

		Assert.Equal(expectedBatchCount, batches.Length);
		Assert.Equal(expectedLastBatchCount, batches[^1].Count);
		Assert.All(batches, batch => Assert.InRange(batch.Count, 1, 24));
		Assert.Equal(count, batches.Sum(batch => batch.Count));
		for (int i = 1; i < batches.Length; i++)
			Assert.Equal(batches[i - 1].Start + batches[i - 1].Count, batches[i].Start);
	}

	[Fact]
	public void CompareFrames_UsesSameAverageAndSkipsMissingPairs() {
		byte[] black = Enumerable.Repeat((byte)0, 32 * 32).ToArray();
		byte[] white = Enumerable.Repeat((byte)255, 32 * 32).ToArray();
		byte[] gray = Enumerable.Repeat((byte)128, 32 * 32).ToArray();
		byte[]?[] source = { black, null, gray };
		byte[]?[] clip = { black, white, white };

		bool passed = PartialVisualVerificationUtils.CompareFrames(
			source,
			clip,
			usePHash: false,
			threshold: 0.70,
			out float similarity);

		float expected = (1f + (1f - 127f / 256f)) / 2f;
		Assert.Equal(expected, similarity, precision: 6);
		Assert.True(passed);
	}

	[Fact]
	public void CompareFrames_NoUsableFramesPreservesAudioPassThrough() {
		bool passed = PartialVisualVerificationUtils.CompareFrames(
			new byte[]?[] { null, null, null },
			new byte[]?[] { null, null, null },
			usePHash: false,
			threshold: 0.85,
			out float similarity);

		Assert.True(passed);
		Assert.Equal(0f, similarity);
	}

	[Fact]
	public void Settings_DefaultVisualParallelismIsSix() {
		Assert.Equal(6, new Settings().PartialClipVisualMaxDegreeOfParallelism);
	}

	[Fact]
	public void Settings_OldJsonKeepsVisualParallelismDefault() {
		Settings settings = System.Text.Json.JsonSerializer.Deserialize(
			"""{"EnablePartialClipDetection":true}""",
			VDF.Core.Utils.CoreJsonContext.Default.Settings)!;

		Assert.Equal(6, settings.PartialClipVisualMaxDegreeOfParallelism);
	}
}
