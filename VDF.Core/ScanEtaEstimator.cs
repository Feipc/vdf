// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

namespace VDF.Core {
	/// <summary>
	/// Estimates scan ETA from a bounded window of recent completions. Hashing
	/// workloads are heterogeneous, so the all-time average is strongly biased by
	/// cached, invalid and short files that usually complete first.
	/// </summary>
	internal sealed class ScanEtaEstimator {
		readonly record struct Sample(DateTime TimestampUtc, int Completed);

		readonly object sync = new();
		readonly Queue<Sample> samples = new();
		readonly TimeSpan window;
		readonly TimeSpan minimumObservation;
		readonly int minimumCompleted;
		int total;
		int lastCompleted;
		DateTime lastTimestampUtc;

		public ScanEtaEstimator(
			TimeSpan? window = null,
			TimeSpan? minimumObservation = null,
			int minimumCompleted = 8) {
			this.window = window ?? TimeSpan.FromSeconds(90);
			this.minimumObservation = minimumObservation ?? TimeSpan.FromSeconds(15);
			this.minimumCompleted = Math.Max(1, minimumCompleted);
		}

		public void Reset(int total, DateTime utcNow) {
			lock (sync) {
				this.total = Math.Max(0, total);
				lastCompleted = 0;
				lastTimestampUtc = utcNow;
				samples.Clear();
				samples.Enqueue(new Sample(utcNow, 0));
			}
		}

		public TimeSpan Estimate(int completed, DateTime utcNow) {
			lock (sync) {
				if (total <= 0)
					return TimeSpan.Zero;

				completed = Math.Clamp(completed, 0, total);
				completed = Math.Max(completed, lastCompleted);
				lastCompleted = completed;
				if (completed >= total)
					return TimeSpan.Zero;

				if (utcNow < lastTimestampUtc)
					utcNow = lastTimestampUtc;
				lastTimestampUtc = utcNow;
				samples.Enqueue(new Sample(utcNow, completed));

				DateTime cutoff = utcNow - window;
				while (samples.Count > 1 && samples.Peek().TimestampUtc < cutoff)
					samples.Dequeue();

				Sample oldest = samples.Peek();
				TimeSpan observation = utcNow - oldest.TimestampUtc;
				int completedInWindow = completed - oldest.Completed;
				if (observation < minimumObservation || completedInWindow < minimumCompleted)
					return TimeSpan.Zero;

				double rate = completedInWindow / observation.TotalSeconds;
				if (!double.IsFinite(rate) || rate <= 0)
					return TimeSpan.Zero;

				double remainingSeconds = (total - completed) / rate;
				if (!double.IsFinite(remainingSeconds) || remainingSeconds <= 0)
					return TimeSpan.Zero;
				if (remainingSeconds >= TimeSpan.MaxValue.TotalSeconds)
					return TimeSpan.MaxValue;
				return TimeSpan.FromSeconds(remainingSeconds);
			}
		}
	}
}
