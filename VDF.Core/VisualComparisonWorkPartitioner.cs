// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Collections.Concurrent;
using System.Linq;

namespace VDF.Core {
	/// <summary>
	/// Exposes each video comparison entry as independently stealable work. The
	/// no-buffering partitioner prevents one dense duration bucket from becoming
	/// a single long-running task while the other workers sit idle.
	/// </summary>
	internal static class VisualComparisonWorkPartitioner {
		public static OrderablePartitioner<int> Create(int count) =>
			Partitioner.Create(
				Enumerable.Range(0, Math.Max(0, count)),
				EnumerablePartitionerOptions.NoBuffering);
	}
}
