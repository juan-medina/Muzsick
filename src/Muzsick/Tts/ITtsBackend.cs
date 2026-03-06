// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Muzsick.Tts;

public record VoiceInfo(string Id, string DisplayName, string Category);

public interface ITtsBackend
{
	/// <summary>
	/// Synthesises speech for <paramref name="text"/> and returns raw WAV bytes (PCM).
	/// Returns an empty array if synthesis is not possible.
	/// </summary>
	Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default);

	/// <summary>
	/// Changes the active voice.
	/// </summary>
	void SetVoice(string voice);

	/// <summary>
	/// All voices available in this backend, keyed by voice ID.
	/// Empty if the backend has no voice selection.
	/// </summary>
	IReadOnlyDictionary<string, VoiceInfo> AvailableVoices { get; }
}
