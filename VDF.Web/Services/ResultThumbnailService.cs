// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Collections.Concurrent;

namespace VDF.Web.Services {
	/// <summary>
	/// Bounds FFmpeg work for Results thumbnails, coalesces identical in-flight
	/// requests, and retains JPEGs in a byte-sized LRU cache.
	/// </summary>
	public sealed class ResultThumbnailService : IDisposable {
		internal const int DefaultMaxConcurrentExtractions = 6;
		internal const long DefaultMaxCacheBytes = 512L * 1024 * 1024;

		readonly SemaphoreSlim _extractionGate;
		readonly long _maxCacheBytes;
		readonly object _cacheLock = new();
		readonly Dictionary<ScopedKey, CacheEntry> _cache = new();
		readonly LinkedList<ScopedKey> _leastRecentlyUsed = new();
		readonly ConcurrentDictionary<ScopedKey, Task<byte[]?>> _inflight = new();
		long _cachedBytes;
		long _generation;

		public ResultThumbnailService(
			int maxConcurrentExtractions = DefaultMaxConcurrentExtractions,
			long maxCacheBytes = DefaultMaxCacheBytes) {
			if (maxConcurrentExtractions <= 0)
				throw new ArgumentOutOfRangeException(nameof(maxConcurrentExtractions));
			if (maxCacheBytes <= 0)
				throw new ArgumentOutOfRangeException(nameof(maxCacheBytes));
			_extractionGate = new SemaphoreSlim(
				maxConcurrentExtractions,
				maxConcurrentExtractions);
			_maxCacheBytes = maxCacheBytes;
		}

		internal async Task<byte[]?> GetOrCreateAsync(
			string cacheKey,
			Func<byte[]?> factory,
			CancellationToken requestAborted) {
			ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
			ArgumentNullException.ThrowIfNull(factory);

			var scopedKey = new ScopedKey(Volatile.Read(ref _generation), cacheKey);
			Task<byte[]?> sharedTask;
			while (true) {
				if (TryGetCached(scopedKey, out byte[]? cached))
					return cached;
				if (_inflight.TryGetValue(scopedKey, out Task<byte[]?>? existingTask)) {
					sharedTask = existingTask;
					break;
				}

				var completion = new TaskCompletionSource<byte[]?>(
					TaskCreationOptions.RunContinuationsAsynchronously);
				if (_inflight.TryAdd(scopedKey, completion.Task)) {
					sharedTask = completion.Task;
					_ = ExecuteExtractionAsync(scopedKey, factory, completion);
					break;
				}
			}

			// Only this HTTP request stops waiting when disconnected. The shared
			// extraction continues for other waiters and for the cache.
			return await sharedTask.WaitAsync(requestAborted).ConfigureAwait(false);
		}

		async Task ExecuteExtractionAsync(
			ScopedKey scopedKey,
			Func<byte[]?> factory,
			TaskCompletionSource<byte[]?> completion) {
			try {
				await _extractionGate.WaitAsync().ConfigureAwait(false);
				byte[]? bytes;
				try {
					bytes = await Task.Run(factory).ConfigureAwait(false);
				}
				finally {
					_extractionGate.Release();
				}

				if (bytes is { Length: > 0 })
					AddCached(scopedKey, bytes);
				completion.TrySetResult(bytes);
			}
			catch (Exception ex) {
				completion.TrySetException(ex);
			}
			finally {
				_inflight.TryRemove(scopedKey, out _);
			}
		}

		bool TryGetCached(ScopedKey key, out byte[]? bytes) {
			lock (_cacheLock) {
				if (!_cache.TryGetValue(key, out CacheEntry? entry)) {
					bytes = null;
					return false;
				}
				_leastRecentlyUsed.Remove(entry.Node);
				_leastRecentlyUsed.AddFirst(entry.Node);
				bytes = entry.Bytes;
				return true;
			}
		}

		void AddCached(ScopedKey key, byte[] bytes) {
			if (bytes.LongLength > _maxCacheBytes ||
				key.Generation != Volatile.Read(ref _generation))
				return;

			lock (_cacheLock) {
				if (key.Generation != Volatile.Read(ref _generation))
					return;
				if (_cache.TryGetValue(key, out CacheEntry? existing)) {
					_cachedBytes -= existing.Bytes.LongLength;
					_leastRecentlyUsed.Remove(existing.Node);
					_cache.Remove(key);
				}

				while (_cachedBytes + bytes.LongLength > _maxCacheBytes &&
					_leastRecentlyUsed.Last is { } last) {
					ScopedKey evictedKey = last.Value;
					_leastRecentlyUsed.RemoveLast();
					if (_cache.Remove(evictedKey, out CacheEntry? evicted))
						_cachedBytes -= evicted.Bytes.LongLength;
				}

				LinkedListNode<ScopedKey> node = _leastRecentlyUsed.AddFirst(key);
				_cache.Add(key, new CacheEntry(bytes, node));
				_cachedBytes += bytes.LongLength;
			}
		}

		internal void Clear() {
			Interlocked.Increment(ref _generation);
			lock (_cacheLock) {
				_cache.Clear();
				_leastRecentlyUsed.Clear();
				_cachedBytes = 0;
			}
		}

		public void Dispose() => _extractionGate.Dispose();

		readonly record struct ScopedKey(long Generation, string CacheKey);
		sealed record CacheEntry(
			byte[] Bytes,
			LinkedListNode<ScopedKey> Node);
	}
}
