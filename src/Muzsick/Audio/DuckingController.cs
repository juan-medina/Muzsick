// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Silk.NET.OpenAL;

namespace Muzsick.Audio;

/// <summary>
/// Performs smooth gain fades on an OpenAL source.
/// All OpenAL calls are made under the caller-supplied lock.
/// </summary>
internal class DuckingController(AL al, object alLock, ILogger? logger = null)
{
	// Steps per fade — smaller = smoother, but more AL calls.
	private const int _steps = 20;

	/// <summary>
	/// Linearly interpolates the gain of <paramref name="source"/> from
	/// <paramref name="from"/> to <paramref name="to"/> over
	/// <paramref name="durationMs"/> milliseconds.
	/// Returns early (without restoring gain) if the token is cancelled.
	/// </summary>
	public async Task FadeAsync(
		uint source,
		float from,
		float to,
		int durationMs,
		CancellationToken cancellationToken = default)
	{
		var stepDelayMs = durationMs / _steps;
		var gainStep = (to - from) / _steps;
		var current = from;

		for (var i = 0; i < _steps; i++)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				logger?.LogDebug("DuckingController: fade cancelled at step {Step}/{Steps}", i, _steps);
				return;
			}

			current += gainStep;
			current = Math.Clamp(current, 0f, 1f);

			lock (alLock)
			{
				al.SetSourceProperty(source, SourceFloat.Gain, current);
			}

			try
			{
				await Task.Delay(stepDelayMs, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				logger?.LogDebug("DuckingController: fade delay cancelled at step {Step}/{Steps}", i, _steps);
				return;
			}
		}

		// Snap to exact target value to avoid float drift.
		if (!cancellationToken.IsCancellationRequested)
		{
			lock (alLock)
			{
				al.SetSourceProperty(source, SourceFloat.Gain, to);
			}
		}

		logger?.LogDebug("DuckingController: fade complete {From:F2} → {To:F2} over {Ms} ms", from, to, durationMs);
	}
}

