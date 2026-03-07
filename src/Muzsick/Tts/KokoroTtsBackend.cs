﻿// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Muzsick.Tts;

/// <summary>
/// TTS backend using Sherpa-ONNX + Kokoro-82M.
/// Synthesises speech in-process and returns raw WAV bytes.
/// Model files are expected under Models/KokoroModels/kokoro-en-v0_19/
/// relative to the application base directory.
/// </summary>
public class KokoroTtsBackend : ITtsBackend, IDisposable
{
	private const string _modelSubDir = "kokoro-en-v0_19";
	private const float _speed = 1.0f;

	private readonly ILogger? _logger;
	private readonly OfflineTts? _tts;
	private readonly Dictionary<string, int> _voiceMap;
	private bool _disposed;

	public IReadOnlyDictionary<string, VoiceInfo> AvailableVoices { get; }

	public KokoroTtsBackend(ILogger? logger = null)
	{
		_logger = logger;

		var modelDir = Path.Combine(AppContext.BaseDirectory, "Models", "KokoroModels", _modelSubDir);
		var voicesBinPath = Path.Combine(modelDir, "voices.bin");

		_voiceMap = LoadVoiceMap();
		AvailableVoices = BuildVoiceInfoMap();

		_logger?.LogInformation("KokoroTtsBackend: loaded {Count} voices", _voiceMap.Count);

		if (!Directory.Exists(modelDir))
		{
			_logger?.LogWarning("KokoroTtsBackend: model directory not found at {Path}", modelDir);
			return;
		}

		try
		{
			var config = new OfflineTtsConfig
			{
				Model = new OfflineTtsModelConfig
				{
					Kokoro = new OfflineTtsKokoroModelConfig
					{
						Model = Path.Combine(modelDir, "model.onnx"),
						Voices = voicesBinPath,
						Tokens = Path.Combine(modelDir, "tokens.txt"),
						DataDir = Path.Combine(modelDir, "espeak-ng-data"),
					},
					NumThreads = 2,
					Debug = 0,
					Provider = "cpu",
				},
				MaxNumSentences = 1,
			};

			_tts = new OfflineTts(config);
			_logger?.LogInformation("KokoroTtsBackend: ready");
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "KokoroTtsBackend: failed to initialise");
		}
	}

	public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken = default)
	{
		if (_tts == null || cancellationToken.IsCancellationRequested)
			return Task.FromResult(Array.Empty<byte>());

		return Task.Run(() => Synthesize(text, VoiceToSpeakerId(voice), cancellationToken), cancellationToken);
	}

	private byte[] Synthesize(string text, int speakerId, CancellationToken cancellationToken)
	{
		try
		{
			var audio = _tts!.Generate(text, _speed, speakerId);

			if (cancellationToken.IsCancellationRequested)
				return Array.Empty<byte>();

			var wav = BuildWav(audio.Samples, audio.SampleRate);
			_logger?.LogDebug("KokoroTtsBackend: {Chars} chars → {Bytes} bytes at {Rate} Hz",
				text.Length, wav.Length, audio.SampleRate);
			return wav;
		}
		catch (OperationCanceledException)
		{
			return Array.Empty<byte>();
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "KokoroTtsBackend: synthesis failed");
			return Array.Empty<byte>();
		}
	}

	private int VoiceToSpeakerId(string voice)
	{
		if (_voiceMap.TryGetValue(voice, out var id))
			return id;

		_logger?.LogWarning("KokoroTtsBackend: unknown voice '{Voice}', defaulting to speaker 0", voice);
		return 0;
	}

	private static Dictionary<string, int> LoadVoiceMap()
	{
		return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			{ "af", 0 },
			{ "af_bella", 1 },
			{ "af_nicole", 2 },
			{ "af_sarah", 3 },
			{ "af_sky", 4 },
			{ "am_adam", 5 },
			{ "am_michael", 6 },
			{ "bf_emma", 7 },
			{ "bf_isabella", 8 },
			{ "bm_george", 9 },
			{ "bm_lewis", 10 },
		};
	}

	private static Dictionary<string, VoiceInfo> BuildVoiceInfoMap()
	{
		return new Dictionary<string, VoiceInfo>(StringComparer.OrdinalIgnoreCase)
		{
			{ "af", new VoiceInfo("af", "Default", "American Female") },
			{ "af_bella", new VoiceInfo("af_bella", "Bella", "American Female") },
			{ "af_nicole", new VoiceInfo("af_nicole", "Nicole", "American Female") },
			{ "af_sarah", new VoiceInfo("af_sarah", "Sarah", "American Female") },
			{ "af_sky", new VoiceInfo("af_sky", "Sky", "American Female") },
			{ "am_adam", new VoiceInfo("am_adam", "Adam", "American Male") },
			{ "am_michael", new VoiceInfo("am_michael", "Michael", "American Male") },
			{ "bf_emma", new VoiceInfo("bf_emma", "Emma", "British Female") },
			{ "bf_isabella", new VoiceInfo("bf_isabella", "Isabella", "British Female") },
			{ "bm_george", new VoiceInfo("bm_george", "George", "British Male") },
			{ "bm_lewis", new VoiceInfo("bm_lewis", "Lewis", "British Male") },
		};
	}

	/// <summary>
	/// Encodes float PCM samples as a 32-bit IEEE float WAV file in memory.
	/// </summary>
	private static byte[] BuildWav(float[] samples, int sampleRate)
	{
		const short channels = 1;
		const short bitsPerSample = 32;
		const short audioFormat = 3; // WAVE_FORMAT_IEEE_FLOAT

		var dataBytes = samples.Length * sizeof(float);

		using var ms = new MemoryStream(44 + dataBytes);
		using var w = new BinaryWriter(ms);

		w.Write(new[] { 'R', 'I', 'F', 'F' });
		w.Write(36 + dataBytes);
		w.Write(new[] { 'W', 'A', 'V', 'E' });
		w.Write(new[] { 'f', 'm', 't', ' ' });
		w.Write(16);
		w.Write(audioFormat);
		w.Write(channels);
		w.Write(sampleRate);
		w.Write(sampleRate * channels * bitsPerSample / 8);
		w.Write((short)(channels * bitsPerSample / 8));
		w.Write(bitsPerSample);
		w.Write(new[] { 'd', 'a', 't', 'a' });
		w.Write(dataBytes);
		foreach (var s in samples)
			w.Write(s);

		return ms.ToArray();
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_tts?.Dispose();
	}
}
