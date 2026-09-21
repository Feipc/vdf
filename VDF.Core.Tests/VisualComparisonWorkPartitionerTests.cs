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
			using var firstWorkersReady = new CountdownEvent(workers);
			int entered = 0;
			int active = 0;
			int peakActive = 0;

			Parallel.ForEach(
				VisualComparisonWorkPartitioner.Create(64),
				new ParallelOptions { MaxDegreeOfParallelism = workers },
				_ => {
					int currentActive = Interlocked.Increment(ref active);
					UpdateMaximum(ref peakActive, currentActive);
					try {
						int ordinal = Interlocked.Increment(ref entered);
						if (ordinal <= workers) {
							firstWorkersReady.Signal();
							Assert.True(firstWorkersReady.Wait(TimeSpan.FromSeconds(5)));
						}
						Thread.SpinWait(10_000);
					}
					finally {
						Interlocked.Decrement(ref active);
					}
				});

			Assert.Equal(workers, peakActive);
		}

		static void UpdateMaximum(ref int maximum, int value) {
			int current;
			do {
				current = Volatile.Read(ref maximum);
				if (current >= value)
					return;
			}
			while (Interlocked.CompareExchange(ref maximum, value, current) != current);
		}
	}
}
