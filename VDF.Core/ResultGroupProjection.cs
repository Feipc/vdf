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
using VDF.Core.ViewModels;

namespace VDF.Core {
	internal enum ResultSimilarityFilter {
		All,
		DisplayedHundredPercent,
		BelowDisplayedHundredPercent
	}

	internal enum ResultGroupSort {
		SimilarityDescending,
		SimilarityAscending
	}

	/// <summary>
	/// Immutable, precomputed presentation data for one duplicate group.
	/// </summary>
	internal sealed class ResultGroupView {
		internal Guid GroupId { get; }
		internal IReadOnlyList<DuplicateItem> Items { get; }
		internal float MinimumSimilarity { get; }
		internal float MaximumSimilarity { get; }
		internal bool IsDisplayedHundredPercent { get; }
		internal long TotalSize { get; }
		internal long PotentialSavings { get; }
		internal int FileCount => Items.Count;

		ResultGroupView(
			Guid groupId,
			IReadOnlyList<DuplicateItem> items,
			float minimumSimilarity,
			float maximumSimilarity,
			long totalSize,
			long potentialSavings) {
			GroupId = groupId;
			Items = items;
			MinimumSimilarity = minimumSimilarity;
			MaximumSimilarity = maximumSimilarity;
			IsDisplayedHundredPercent =
				ResultPresentationUtils.FormatSimilarityScore(minimumSimilarity) == "100.0";
			TotalSize = totalSize;
			PotentialSavings = potentialSavings;
		}

		internal static ResultGroupView Create(IEnumerable<DuplicateItem> source) {
			List<DuplicateItem> items = source.ToList();
			if (items.Count == 0)
				throw new ArgumentException("A result group must contain at least one item.", nameof(source));

			float[] scores = items
				.Where(item => !ResultPresentationUtils.IsSimilarityReference(item, items))
				.Select(item => item.Similarity)
				.ToArray();
			if (scores.Length == 0)
				scores = items.Select(item => item.Similarity).ToArray();

			long totalSize = 0;
			long largest = 0;
			foreach (DuplicateItem item in items) {
				long size = Math.Max(0, item.SizeLong);
				totalSize += size;
				largest = Math.Max(largest, size);
			}

			return new ResultGroupView(
				items[0].GroupId,
				items,
				scores.Min(),
				scores.Max(),
				totalSize,
				totalSize - largest);
		}
	}

	/// <summary>
	/// Deterministic grouping, filtering, sorting and paging for large result sets.
	/// </summary>
	internal static class ResultGroupProjection {
		internal static IReadOnlyList<ResultGroupView> Build(
			IEnumerable<DuplicateItem> items) =>
			items
				.GroupBy(item => item.GroupId)
				.Select(ResultGroupView.Create)
				.ToList();

		internal static IReadOnlyList<ResultGroupView> FilterAndSort(
			IReadOnlyList<ResultGroupView> groups,
			string search,
			string? folder,
			ResultSimilarityFilter similarityFilter,
			ResultGroupSort sort,
			IReadOnlyCollection<string>? excludedDirectories = null) {
			var filtered = new List<ResultGroupView>(groups.Count);
			foreach (ResultGroupView originalGroup in groups) {
				ResultGroupView group = originalGroup;
				if (excludedDirectories is { Count: > 0 }) {
					List<DuplicateItem> includedItems = originalGroup.Items
						.Where(item => !excludedDirectories.Any(directory =>
							ResultPresentationUtils.IsInDirectory(item.Path, directory)))
						.ToList();
					if (includedItems.Count < 2)
						continue;
					group = ResultGroupView.Create(includedItems);
				}

				if (!string.IsNullOrWhiteSpace(folder)) {
					List<DuplicateItem> folderItems = group.Items
						.Where(item => ResultPresentationUtils.IsInDirectory(item.Path, folder))
						.ToList();
					if (folderItems.Count == 0)
						continue;
					group = ResultGroupView.Create(folderItems);
				}

				if (!string.IsNullOrEmpty(search) &&
					!group.Items.Any(item =>
						ResultPresentationUtils.PathMatchesFilter(item.Path, search)))
					continue;

				if (similarityFilter == ResultSimilarityFilter.DisplayedHundredPercent &&
					!group.IsDisplayedHundredPercent)
					continue;
				if (similarityFilter == ResultSimilarityFilter.BelowDisplayedHundredPercent &&
					group.IsDisplayedHundredPercent)
					continue;

				filtered.Add(group);
			}

			IOrderedEnumerable<ResultGroupView> ordered = sort switch {
				ResultGroupSort.SimilarityAscending =>
					filtered.OrderBy(group => group.MinimumSimilarity),
				_ => filtered.OrderByDescending(group => group.MinimumSimilarity)
			};
			return ordered
				.ThenByDescending(group => group.PotentialSavings)
				.ThenBy(group => group.GroupId)
				.ToList();
		}

		internal static int GetPageCount(int itemCount, int pageSize) {
			if (pageSize <= 0)
				throw new ArgumentOutOfRangeException(nameof(pageSize));
			return Math.Max(1, (itemCount + pageSize - 1) / pageSize);
		}

		internal static int ClampPage(int page, int itemCount, int pageSize) =>
			Math.Clamp(page, 1, GetPageCount(itemCount, pageSize));

		internal static IReadOnlyList<ResultGroupView> GetPage(
			IReadOnlyList<ResultGroupView> groups,
			int page,
			int pageSize) {
			int clampedPage = ClampPage(page, groups.Count, pageSize);
			return groups
				.Skip((clampedPage - 1) * pageSize)
				.Take(pageSize)
				.ToList();
		}
	}
}
