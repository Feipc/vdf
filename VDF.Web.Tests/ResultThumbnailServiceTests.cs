using VDF.Web.Services;

namespace VDF.Web.Tests;

public sealed class ResultThumbnailServiceTests {
	[Fact]
	public async Task GetOrCreateAsync_CoalescesConcurrentRequestsForSameKey() {
		using var service = new ResultThumbnailService(
			maxConcurrentExtractions: 6,
			maxCacheBytes: 1024);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int calls = 0;

		byte[] Factory() {
			Interlocked.Increment(ref calls);
			release.Task.GetAwaiter().GetResult();
			return [1, 2, 3];
		}

		Task<byte[]?>[] requests = Enumerable.Range(0, 20)
			.Select(_ => service.GetOrCreateAsync("same", Factory, CancellationToken.None))
			.ToArray();
		Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 10000));
		release.SetResult();

		byte[]?[] results = await Task.WhenAll(requests);
		Assert.Equal(1, calls);
		Assert.All(results, result =>
			Assert.Equal(new byte[] { 1, 2, 3 }, result));
	}

	[Fact]
	public async Task GetOrCreateAsync_CancelledWaiterDoesNotCancelSharedExtraction() {
		using var service = new ResultThumbnailService(
			maxConcurrentExtractions: 1,
			maxCacheBytes: 1024);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int calls = 0;

		byte[] Factory() {
			Interlocked.Increment(ref calls);
			release.Task.GetAwaiter().GetResult();
			return [7];
		}

		Task<byte[]?> owner = service.GetOrCreateAsync(
			"shared", Factory, CancellationToken.None);
		Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 10000));
		using var cancelled = new CancellationTokenSource();
		Task<byte[]?> waiter = service.GetOrCreateAsync(
			"shared", Factory, cancelled.Token);
		cancelled.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
		release.SetResult();
		Assert.Equal(new byte[] { 7 }, await owner);
		Assert.Equal(new byte[] { 7 }, await service.GetOrCreateAsync(
			"shared", Factory, CancellationToken.None));
		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task GetOrCreateAsync_LimitsDifferentKeysToConfiguredConcurrency() {
		using var service = new ResultThumbnailService(
			maxConcurrentExtractions: 6,
			maxCacheBytes: 1024);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int active = 0;
		int maximumActive = 0;

		byte[] Factory() {
			int nowActive = Interlocked.Increment(ref active);
			int observed;
			do {
				observed = Volatile.Read(ref maximumActive);
				if (observed >= nowActive)
					break;
			} while (Interlocked.CompareExchange(
				ref maximumActive, nowActive, observed) != observed);
			release.Task.GetAwaiter().GetResult();
			Interlocked.Decrement(ref active);
			return [1];
		}

		Task<byte[]?>[] requests = Enumerable.Range(0, 18)
			.Select(index => service.GetOrCreateAsync(
				$"key-{index}", Factory, CancellationToken.None))
			.ToArray();
		Assert.True(SpinWait.SpinUntil(
			() => Volatile.Read(ref maximumActive) == 6, 10000));
		Assert.Equal(6, Volatile.Read(ref active));
		release.SetResult();
		await Task.WhenAll(requests);

		Assert.Equal(6, maximumActive);
	}

	[Fact]
	public async Task GetOrCreateAsync_EvictsLeastRecentlyUsedEntriesWithoutClearingCache() {
		using var service = new ResultThumbnailService(
			maxConcurrentExtractions: 1,
			maxCacheBytes: 8);
		var calls = new Dictionary<string, int>();

		Task<byte[]?> Get(string key) => service.GetOrCreateAsync(
			key,
			() => {
				calls[key] = calls.GetValueOrDefault(key) + 1;
				return new byte[4];
			},
			CancellationToken.None);

		await Get("a");
		await Get("b");
		await Get("a"); // a is now the most recently used entry.
		await Get("c"); // evicts b only.
		await Get("a");
		await Get("b");

		Assert.Equal(1, calls["a"]);
		Assert.Equal(2, calls["b"]);
		Assert.Equal(1, calls["c"]);
	}

	[Fact]
	public async Task Clear_PreventsInFlightWorkFromRepopulatingNewGeneration() {
		using var service = new ResultThumbnailService(
			maxConcurrentExtractions: 1,
			maxCacheBytes: 1024);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int calls = 0;

		Task<byte[]?> first = service.GetOrCreateAsync(
			"frame",
			() => {
				Interlocked.Increment(ref calls);
				release.Task.GetAwaiter().GetResult();
				return [1];
			},
			CancellationToken.None);
		Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 10000));
		service.Clear();
		release.SetResult();
		await first;

		await service.GetOrCreateAsync(
			"frame",
			() => {
				Interlocked.Increment(ref calls);
				return [2];
			},
			CancellationToken.None);

		Assert.Equal(2, calls);
	}
}
