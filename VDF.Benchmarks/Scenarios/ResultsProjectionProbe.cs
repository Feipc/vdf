// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Diagnostics;
using VDF.Core;
using VDF.Core.ViewModels;

namespace VDF.Benchmarks.Scenarios;

internal static class ResultsProjectionProbe {
	internal static int Run(string[] args) {
		int itemCount = args.Length > 1 && int.TryParse(args[1], out int parsed)
			? Math.Max(6, parsed)
			: 6000;
		const int itemsPerGroup = 3;
		const int iterations = 10;
		List<DuplicateItem> items = CreateItems(itemCount, itemsPerGroup);

		// Warm the JIT and collection paths before recording.
		IReadOnlyList<ResultGroupView> warmGroups = ResultGroupProjection.Build(items);
		_ = ResultGroupProjection.FilterAndSort(
			warmGroups,
			"video",
			null,
			ResultSimilarityFilter.All,
			ResultGroupSort.SimilarityDescending);

		var buildTimes = new List<double>(iterations);
		var filterTimes = new List<double>(iterations);
		var pageTimes = new List<double>(iterations);
		IReadOnlyList<ResultGroupView> groups = warmGroups;
		IReadOnlyList<ResultGroupView> filtered = warmGroups;
		IReadOnlyList<ResultGroupView> page = [];

		for (int iteration = 0; iteration < iterations; iteration++) {
			var stopwatch = Stopwatch.StartNew();
			groups = ResultGroupProjection.Build(items);
			stopwatch.Stop();
			buildTimes.Add(stopwatch.Elapsed.TotalMilliseconds);

			stopwatch.Restart();
			filtered = ResultGroupProjection.FilterAndSort(
				groups,
				"video",
				null,
				ResultSimilarityFilter.All,
				ResultGroupSort.SimilarityDescending);
			stopwatch.Stop();
			filterTimes.Add(stopwatch.Elapsed.TotalMilliseconds);

			stopwatch.Restart();
			page = ResultGroupProjection.GetPage(filtered, 1, 50);
			stopwatch.Stop();
			pageTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
		}

		Console.WriteLine("== Results projection probe ==");
		Console.WriteLine(
			$"items: {items.Count:N0}, groups: {groups.Count:N0}, iterations: {iterations}");
		Print("group build", buildTimes);
		Print("filter + stable sort", filterTimes);
		Print("page projection", pageTimes);
		Console.WriteLine(
			$"first page: {page.Count} groups, {page.Sum(group => group.FileCount)} cards");
		return 0;
	}

	static List<DuplicateItem> CreateItems(int requestedCount, int itemsPerGroup) {
		int groupCount = (requestedCount + itemsPerGroup - 1) / itemsPerGroup;
		var items = new List<DuplicateItem>(groupCount * itemsPerGroup);
		for (int groupIndex = 0; groupIndex < groupCount; groupIndex++) {
			Guid groupId = GuidFromInt(groupIndex);
			for (int member = 0; member < itemsPerGroup; member++) {
				float similarity = member == 0
					? 100f
					: 90f + ((groupIndex + member) % 101) / 10f;
				items.Add(new DuplicateItem {
					GroupId = groupId,
					Path = $"/media/bucket-{groupIndex % 20}/video-{groupIndex:D5}-{member}.mp4",
					Folder = $"/media/bucket-{groupIndex % 20}",
					Similarity = Math.Min(100f, similarity),
					SizeLong = 10_000_000L + groupIndex * 1000L + member
				});
			}
		}
		return items.Take(requestedCount).ToList();
	}

	static Guid GuidFromInt(int value) {
		byte[] bytes = new byte[16];
		BitConverter.GetBytes(value).CopyTo(bytes, 0);
		return new Guid(bytes);
	}

	static void Print(string label, List<double> measurements) {
		measurements.Sort();
		double median = measurements[measurements.Count / 2];
		Console.WriteLine(
			$"{label,-22} median {median,8:F2} ms " +
			$"(min {measurements[0]:F2}, max {measurements[^1]:F2})");
	}
}
