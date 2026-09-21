// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

namespace VDF.Core {
	public enum ComparisonStage {
		VisualComparison,
		PartialCandidatePlanning,
		PartialIndexBuilding,
		PartialCandidateSearch,
		PartialAudioComparison,
		PartialExactVerification,
		PartialVisualVerification,
		PartialVisualSourceSampling,
		PartialVisualClipVerification,
		GroupingResults,
		SavingDatabase,
	}

	public sealed class ComparisonProgressChangedEventArgs : EventArgs {
		public ComparisonStage Stage { get; init; }
		public long Current { get; init; }
		public long Total { get; init; }
		public double ItemsPerSecond { get; init; }
		public TimeSpan Elapsed { get; init; }
		public TimeSpan Remaining { get; init; }
		public int ActiveWorkers { get; init; }
		public int EffectiveParallelism { get; init; }
	}
}
