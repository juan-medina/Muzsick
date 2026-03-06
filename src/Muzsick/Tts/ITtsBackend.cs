// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;

namespace Muzsick.Tts;

public interface ITtsBackend
{
	/// <summary>
	/// Synthesises speech for <paramref name="text"/> and returns raw WAV bytes (PCM).
	/// Returns an empty array if synthesis is not possible.
	/// </summary>
	Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
}

