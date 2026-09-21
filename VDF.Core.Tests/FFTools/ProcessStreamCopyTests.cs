using VDF.Core.FFTools;

namespace VDF.Core.Tests.FFTools {
	public sealed class ProcessStreamCopyTests {
		[Fact]
		public void CopyTo_CompletesNormallyBeforeTheDeadline() {
			byte[] expected = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
			using var source = new MemoryStream(expected);
			using var destination = new MemoryStream();
			int timeoutCalls = 0;

			bool completed = ProcessStreamCopy.CopyTo(
				source,
				destination,
				TimeSpan.FromSeconds(1),
				() => Interlocked.Increment(ref timeoutCalls));

			Assert.True(completed);
			Assert.Equal(0, timeoutCalls);
			Assert.Equal(expected, destination.ToArray());
		}

		[Fact(Timeout = 10_000)]
		public async Task CopyTo_TerminatesAProcessWhosePipeStopsProducingOutput() {
			using var source = new NeverCompletingReadStream();
			using var destination = new MemoryStream();
			int timeoutCalls = 0;

			bool completed = await Task.Run(() => ProcessStreamCopy.CopyTo(
				source,
				destination,
				TimeSpan.FromMilliseconds(50),
				() => Interlocked.Increment(ref timeoutCalls)));

			Assert.False(completed);
			Assert.Equal(1, timeoutCalls);
		}

		sealed class NeverCompletingReadStream : Stream {
			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position {
				get => throw new NotSupportedException();
				set => throw new NotSupportedException();
			}

			public override int Read(byte[] buffer, int offset, int count) =>
				throw new NotSupportedException();

			public override async ValueTask<int> ReadAsync(
				Memory<byte> buffer,
				CancellationToken cancellationToken = default) {
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return 0;
			}

			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}
	}
}
