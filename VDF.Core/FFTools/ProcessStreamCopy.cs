// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

namespace VDF.Core.FFTools {
	/// <summary>
	/// Copies a redirected process pipe while enforcing a real deadline. Waiting
	/// synchronously for EOF before calling Process.WaitForExit leaves the declared
	/// process timeout unreachable when FFmpeg or FFprobe stops producing output.
	/// </summary>
	internal static class ProcessStreamCopy {
		public static bool CopyTo(
			Stream source,
			Stream destination,
			TimeSpan timeout,
			Action terminateProcess) {
			using var cancellation = new CancellationTokenSource();
			Task copy = source.CopyToAsync(destination, cancellation.Token);
			using WaitHandle completed = ((IAsyncResult)copy).AsyncWaitHandle;

			// CancellationTokenSource.CancelAfter dispatches its timer through the thread
			// pool. Under a saturated comparison/test workload that callback can arrive
			// seconds late, leaving the process pipe blocked beyond its deadline. A wait
			// handle enforces the deadline independently of thread-pool availability.
			if (!completed.WaitOne(timeout)) {
				try { terminateProcess(); } catch { }
				cancellation.Cancel();
				// Some platform streams finish asynchronously after the child is killed.
				// Observe a later fault without extending the caller's deadline.
				_ = copy.ContinueWith(
					static task => _ = task.Exception,
					CancellationToken.None,
					TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
				return false;
			}

			try {
				copy.GetAwaiter().GetResult();
				return true;
			}
			finally { cancellation.Cancel(); }
		}
	}
}
