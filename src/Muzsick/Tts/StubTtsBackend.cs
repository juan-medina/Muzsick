// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Muzsick.Tts;

/// <summary>
/// Stub TTS backend used during V0 development.
/// Ignores the input text and returns the bytes of <c>test.wav</c> from the
/// application base directory. No synthesis is performed.
/// </summary>
public class StubTtsBackend(ILogger? logger = null) : ITtsBackend
{
	private const string _fileName = "test.wav";

	public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
			return Task.FromResult(Array.Empty<byte>());

		var path = Path.Combine(AppContext.BaseDirectory, _fileName);

		if (!File.Exists(path))
		{
			logger?.LogWarning("StubTtsBackend: {File} not found at {Path}", _fileName, path);
			return Task.FromResult(Array.Empty<byte>());
		}

		try
		{
			var bytes = File.ReadAllBytes(path);
			logger?.LogDebug("StubTtsBackend: loaded {Bytes} bytes from {File}", bytes.Length, _fileName);
			return Task.FromResult(bytes);
		}
		catch (Exception ex)
		{
			logger?.LogWarning(ex, "StubTtsBackend: failed to read {File}", _fileName);
			return Task.FromResult(Array.Empty<byte>());
		}
	}
}

