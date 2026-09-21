// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using VDF.Core.ViewModels;

namespace VDF.Core.Tests;

public class ResultPresentationUtilsTests {
	[Fact]
	public void ThumbnailPositions_FiveFramesUseScanFractions() {
		var settings = new Settings { ThumbnailCount = 5 };
		var item = Item("/media/video.mp4", durationSeconds: 600);

		Assert.Equal(5, ThumbnailPositionResolver.GetFrameCount(item, settings));
		Assert.True(ThumbnailPositionResolver.TryGetPosition(item, settings, 0, out TimeSpan first));
		Assert.True(ThumbnailPositionResolver.TryGetPosition(item, settings, 4, out TimeSpan last));
		Assert.Equal(TimeSpan.FromSeconds(100), first);
		Assert.Equal(TimeSpan.FromSeconds(500), last);
	}

	[Fact]
	public void ThumbnailPositions_HonorSamplingDurationAndStoredTimestamps() {
		var settings = new Settings {
			ThumbnailCount = 5,
			MaxSamplingDurationSeconds = 300,
		};
		var item = Item("/media/video.mp4", durationSeconds: 600);
		item.ThumbnailTimestamps.Add(TimeSpan.FromSeconds(12));

		Assert.True(ThumbnailPositionResolver.TryGetPosition(item, settings, 0, out TimeSpan stored));
		Assert.True(ThumbnailPositionResolver.TryGetPosition(item, settings, 4, out TimeSpan derived));
		Assert.Equal(TimeSpan.FromSeconds(12), stored);
		Assert.Equal(TimeSpan.FromSeconds(250), derived);
	}

	[Fact]
	public void ThumbnailPositions_ImagesHaveOneFrameAndRejectOtherIndices() {
		var settings = new Settings { ThumbnailCount = 5 };
		var item = Item("/media/image.jpg", durationSeconds: 0, isImage: true);

		Assert.Equal(1, ThumbnailPositionResolver.GetFrameCount(item, settings));
		Assert.True(ThumbnailPositionResolver.TryGetPosition(item, settings, 0, out TimeSpan position));
		Assert.Equal(TimeSpan.Zero, position);
		Assert.False(ThumbnailPositionResolver.TryGetPosition(item, settings, 1, out _));
	}

	[Fact]
	public void GroupSimilarity_ExcludesReferenceAndFormatsRange() {
		var reference = Item("/media/source.mp4", 600);
		reference.Similarity = 100;
		reference.IsSimilarityReference = true;
		var first = Item("/media/clip-a.mp4", 120);
		first.Similarity = 99;
		var second = Item("/media/clip-b.mp4", 90);
		second.Similarity = 93;

		Assert.Equal(
			"Match scores 93.0\u201399.0%",
			ResultPresentationUtils.FormatGroupSimilarity(new[] { reference, first, second }));
	}

	[Fact]
	public void GroupSimilarity_SingleNonReferenceUsesOneValue() {
		var reference = Item("/media/source.mp4", 600);
		reference.IsSimilarityReference = true;
		var clip = Item("/media/clip.mp4", 120);
		clip.Similarity = 97.25f;

		Assert.Equal(
			"Match score 97.3%",
			ResultPresentationUtils.FormatGroupSimilarity(new[] { reference, clip }));
	}

	[Fact]
	public void GroupSimilarity_UsesRangeWhenRoundedScoresDiffer() {
		var first = Item("/media/first.mp4", 120);
		first.Similarity = 99.94f;
		var second = Item("/media/second.mp4", 120);
		second.Similarity = 99.96f;

		Assert.Equal(
			"Match scores 99.9\u2013100.0%",
			ResultPresentationUtils.FormatGroupSimilarity([first, second]));
	}

	[Fact]
	public void LegacyPartialGroup_InfersNonClipItemAsReference() {
		var source = Item("/media/source.mp4", 600);
		source.Similarity = 100;
		var clip = Item("/media/clip.mp4", 120);
		clip.Similarity = 94;
		clip.Flags = DuplicateFlags.PartialClip;
		var group = new[] { source, clip };

		Assert.True(ResultPresentationUtils.IsSimilarityReference(source, group));
		Assert.False(ResultPresentationUtils.IsSimilarityReference(clip, group));
		Assert.Equal("Match score 94.0%", ResultPresentationUtils.FormatGroupSimilarity(group));
	}

	[Fact]
	public void DirectoryFilter_MatchesOnlyExactParentDirectory() {
		Assert.True(ResultPresentationUtils.IsInDirectory(
			"/mnt/Videos/Films/a.mp4",
			"/mnt/Videos/Films"));
		Assert.False(ResultPresentationUtils.IsInDirectory(
			"/mnt/Videos/Films/Sub/b.mp4",
			"/mnt/Videos/Films"));
		Assert.False(ResultPresentationUtils.IsInDirectory(
			"/mnt/Videos/Films-Old/c.mp4",
			"/mnt/Videos/Films"));
	}

	[Fact]
	public void ResultPathLookup_AllowsOnlyPathsPresentInResults() {
		var item = Item("/mnt/Videos/Films/a.mp4", 120);
		var results = new[] { item };

		Assert.Same(
			item,
			ResultPresentationUtils.FindResultByPath(
				results,
				"/mnt/Videos/Films/a.mp4",
				out string normalizedPath));
		Assert.Equal(Path.GetFullPath(item.Path), normalizedPath);
		Assert.Null(ResultPresentationUtils.FindResultByPath(
			results,
			"/mnt/Videos/Films/not-in-results.mp4",
			out _));
	}

	static DuplicateItem Item(
		string path,
		double durationSeconds,
		bool isImage = false) =>
		new() {
			Path = path,
			Duration = TimeSpan.FromSeconds(durationSeconds),
			IsImage = isImage,
		};
}
