using System.Collections.Concurrent;
using VDF.Core;

namespace VDF.Core.Tests {
	public sealed class VisualComparisonWorkPartitionerTests {
		[Fact]
		public void Create_ReturnsEveryIndexExactlyOnce() {
			const int count = 5_001;
			var visited = new ConcurrentBag<int>();

			Parallel.ForEach(
				VisualComparisonWorkPartitioner.Create(count),
				new ParallelOptions { MaxDegreeOfParallelism = 4 },
				index => visited.Add(index));

			Assert.Equal(
				Enumerable.Range(0, count).ToArray(),
				visited.OrderBy(index => index).ToArray());
		}

		[Fact]
		public void Create_ExposesOneLogicalBucketToAllConfiguredWorkers() {
			const int workers = 4;
			var partitions = VisualComparisonWorkPartitioner.Create(64)
				.GetOrderablePartitions(workers);
			try {
				// Exercise the partitioner contract directly. Waiting for Parallel.ForEach
				// to schedule four callbacks at the same instant made this test depend on
				// spare Windows runner threads rather than on the partitioner under test.
				Assert.Equal(workers, partitions.Count);
				Assert.All(partitions, partition => Assert.True(partition.MoveNext()));
				Assert.Equal(
					workers,
					partitions.Select(partition => partition.Current.Value).Distinct().Count());
			}
			finally {
				foreach (var partition in partitions)
					partition.Dispose();
			}
		}
	}
}
