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
			int timedOut = 0;
			using CancellationTokenRegistration registration = cancellation.Token.Register(() => {
				if (Interlocked.Exchange(ref timedOut, 1) != 0)
					return;
				try { terminateProcess(); } catch { }
			});
			cancellation.CancelAfter(timeout);

			try {
				source.CopyToAsync(destination, cancellation.Token).GetAwaiter().GetResult();
				return Volatile.Read(ref timedOut) == 0;
			}
			catch (OperationCanceledException) when (Volatile.Read(ref timedOut) != 0) {
				return false;
			}
			catch (Exception) when (Volatile.Read(ref timedOut) != 0) {
				// Killing the child closes its redirected pipe. Depending on the
				// platform, the pending read completes as cancellation, EPIPE or
				// an object-disposed exception.
				return false;
			}
		}
	}
}
