using VDF.Core;

namespace VDF.Core.Tests {
	public sealed class ScanEtaEstimatorTests {
		[Fact]
		public void Estimate_ReturnsUnknownUntilTheObservationWindowIsWarm() {
			DateTime started = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);
			var estimator = new ScanEtaEstimator(
				window: TimeSpan.FromSeconds(60),
				minimumObservation: TimeSpan.FromSeconds(10),
				minimumCompleted: 8);
			estimator.Reset(100, started);

			TimeSpan remaining = estimator.Estimate(20, started.AddSeconds(5));

			Assert.Equal(TimeSpan.Zero, remaining);
		}

		[Fact]
		public void Estimate_UsesRecentThroughputInsteadOfTheFastStartupAverage() {
			DateTime started = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);
			var estimator = new ScanEtaEstimator(
				window: TimeSpan.FromSeconds(60),
				minimumObservation: TimeSpan.FromSeconds(10),
				minimumCompleted: 8);
			estimator.Reset(1000, started);
			estimator.Estimate(600, started.AddSeconds(60));

			TimeSpan remaining = estimator.Estimate(660, started.AddSeconds(120));

			Assert.Equal(TimeSpan.FromSeconds(340), remaining);
		}

		[Fact]
		public void Estimate_UsesTheActualCompletedCountWithoutAnOffByOne() {
			DateTime started = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);
			var estimator = new ScanEtaEstimator(
				window: TimeSpan.FromMinutes(5),
				minimumObservation: TimeSpan.FromSeconds(10),
				minimumCompleted: 8);
			estimator.Reset(100, started);

			TimeSpan remaining = estimator.Estimate(20, started.AddSeconds(20));

			Assert.Equal(TimeSpan.FromSeconds(80), remaining);
		}

		[Fact]
		public void Estimate_ReturnsUnknownWhenNoFilesCompleteInTheRecentWindow() {
			DateTime started = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);
			var estimator = new ScanEtaEstimator(
				window: TimeSpan.FromSeconds(60),
				minimumObservation: TimeSpan.FromSeconds(10),
				minimumCompleted: 8);
			estimator.Reset(100, started);
			estimator.Estimate(50, started.AddSeconds(60));

			TimeSpan remaining = estimator.Estimate(50, started.AddSeconds(121));

			Assert.Equal(TimeSpan.Zero, remaining);
		}

		[Fact]
		public void Estimate_ReturnsZeroWhenAllFilesAreComplete() {
			DateTime started = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);
			var estimator = new ScanEtaEstimator();
			estimator.Reset(100, started);

			TimeSpan remaining = estimator.Estimate(100, started.AddMinutes(1));

			Assert.Equal(TimeSpan.Zero, remaining);
		}
	}
}
