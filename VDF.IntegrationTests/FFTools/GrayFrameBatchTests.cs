// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using VDF.Core.FFTools;
using VDF.IntegrationTests.Fixtures;

namespace VDF.IntegrationTests.FFTools;

[Collection("Ffmpeg")]
public class GrayFrameBatchTests {
	readonly FfmpegFixture fixture;

	public GrayFrameBatchTests(FfmpegFixture fixture) => this.fixture = fixture;

	[SkippableTheory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(24)]
	[InlineData(25)]
	public void ProcessBatch_IsByteIdenticalToPerFrameExtraction(int frameCount) {
		Skip.If(!fixture.FfmpegCliAvailable, fixture.FfmpegNotFoundReason);
		Skip.If(fixture.H264_8bit == null, "H264 test video not generated");

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = false;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;
		double[] positions = Enumerable.Range(1, frameCount)
			.Select(index => index * 1.8 / (frameCount + 1))
			.ToArray();

		byte[]?[] expected = positions
			.Select(position => FfmpegEngine.GetThumbnail(new FfmpegSettings {
				File = fixture.H264_8bit!,
				Position = TimeSpan.FromSeconds(position),
				GrayScale = 1,
			}, extendedLogging: false))
			.ToArray();
		var stats = new GrayFrameExtractionStats();
		byte[]?[] actual = FfmpegEngine.GetGrayFrames(
			fixture.H264_8bit!,
			positions,
			extendedLogging: false,
			stats);

		Assert.All(expected, frame => Assert.NotNull(frame));
		for (int i = 0; i < expected.Length; i++)
			Assert.Equal(expected[i], actual[i]);
		Assert.Equal((frameCount + 23) / 24, stats.CliBatches);
		Assert.Equal(0, stats.FallbackFrames);
	}

	[SkippableFact]
	public void ProcessBatch_PreservesCustomVideoFilter() {
		Skip.If(!fixture.FfmpegCliAvailable, fixture.FfmpegNotFoundReason);
		Skip.If(fixture.H264_8bit == null, "H264 test video not generated");

		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = false;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = "-vf hflip";
		double[] positions = { 0.25, 0.75, 1.25 };

		byte[]?[] expected = positions
			.Select(position => FfmpegEngine.GetThumbnail(new FfmpegSettings {
				File = fixture.H264_8bit!,
				Position = TimeSpan.FromSeconds(position),
				GrayScale = 1,
			}, extendedLogging: false))
			.ToArray();
		var stats = new GrayFrameExtractionStats();
		byte[]?[] actual = FfmpegEngine.GetGrayFrames(
			fixture.H264_8bit!,
			positions,
			extendedLogging: false,
			stats);

		Assert.All(expected, frame => Assert.NotNull(frame));
		for (int i = 0; i < expected.Length; i++)
			Assert.Equal(expected[i], actual[i]);
		Assert.Equal(1, stats.CliBatches);
		Assert.Equal(0, stats.FallbackFrames);
	}
}
