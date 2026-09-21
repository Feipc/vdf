using VDF.Core;
using VDF.Core.Utils;

namespace VDF.Core.Tests {
	[Collection(DatabaseTestCollection.Name)]
	public sealed class VisualComparisonSchedulingTests {
		[Fact]
		public void BucketedComparison_ProducesIdenticalGroupsAtOneAndFourWorkers() {
			string[] singleWorker = RunComparison(workers: 1);
			string[] fourWorkers = RunComparison(workers: 4);

			Assert.NotEmpty(singleWorker);
			Assert.Equal(singleWorker, fourWorkers);
		}

		static string[] RunComparison(int workers) {
			DatabaseUtils.Database.Clear();
			try {
				foreach (FileEntry entry in BuildCorpus(5_001))
					DatabaseUtils.Database.Add(entry);

				var engine = new ScanEngine();
				engine.Settings.ThumbnailCount = 1;
				engine.Settings.Percent = 96f;
				engine.Settings.PercentDurationDifference = 0;
				engine.Settings.DurationDifferenceMinSeconds = 0;
				engine.Settings.DurationDifferenceMaxSeconds = 0;
				engine.Settings.MaxDegreeOfParallelism = workers;
				engine.Settings.VisualCompareMaxDegreeOfParallelism = workers;
				engine.Settings.UsePHashing = false;
				engine.EnsureThumbnailPositions();

				engine.ScanForDuplicates();

				return engine.Duplicates
					.GroupBy(item => item.GroupId)
					.Select(group => string.Join(
						";",
						group
							.Select(item =>
								$"{item.Path}|{item.Similarity:R}|{(int)item.Flags}")
							.OrderBy(value => value, StringComparer.Ordinal)))
					.OrderBy(value => value, StringComparer.Ordinal)
					.ToArray();
			}
			finally {
				DatabaseUtils.Database.Clear();
			}
		}

		static IEnumerable<FileEntry> BuildCorpus(int count) {
			for (int i = 0; i < count; i++) {
				int patternId = i < 20 ? i / 2 : i;
				double durationSeconds = i < 20 ? 100 + i / 2 : 1_000 + i;
				var gray = new byte[32 * 32];
				var random = new Random(patternId + 17);
				random.NextBytes(gray);

				var entry = new FileEntry {
					_Path = $"/virtual/video-{i:D5}.mp4",
					Folder = "/virtual",
					FileSize = 1_000_000 + i,
					mediaInfo = new MediaInfo {
						Duration = TimeSpan.FromSeconds(durationSeconds),
						Streams = new[] {
							new MediaInfo.StreamInfo {
								CodecType = "video",
								CodecName = "h264",
								Width = 1280,
								Height = 720,
								FrameRate = 25,
								BitRate = 4_000_000,
							}
						}
					},
					invalid = false,
				};
				double position = entry.GetGrayBytesIndex(0.5f, 0d);
				entry.grayBytes[position] = gray;
				entry.PHashes[position] =
					VDF.Core.pHash.PerceptualHash.ComputePHashFromGray32x32(gray);
				yield return entry;
			}
		}
	}
}
