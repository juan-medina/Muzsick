// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
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
	private readonly int _speakerId;
	private readonly OfflineTts? _tts;
	private bool _disposed;

	public KokoroTtsBackend(string voice = "af_heart", ILogger? logger = null)
	{
		_logger = logger;
		_speakerId = VoiceToSpeakerId(voice);

		var modelDir = Path.Combine(AppContext.BaseDirectory, "Models", "KokoroModels", _modelSubDir);

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
						Voices = Path.Combine(modelDir, "voices.bin"),
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
			_logger?.LogInformation("KokoroTtsBackend: ready, speaker id={Id}", _speakerId);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "KokoroTtsBackend: failed to initialise");
		}
	}

	public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
	{
		if (_tts == null || cancellationToken.IsCancellationRequested)
			return Task.FromResult(Array.Empty<byte>());

		return Task.Run(() => Synthesize(text, cancellationToken), cancellationToken);
	}

	private byte[] Synthesize(string text, CancellationToken cancellationToken)
	{
		try
		{
			var audio = _tts!.Generate(text, _speed, _speakerId);

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

	private static int VoiceToSpeakerId(string voice) => voice switch
	{
		"af_heart"    => 0,
		"af_alloy"    => 1,
		"af_aoede"    => 2,
		"af_bella"    => 3,
		"af_jessica"  => 4,
		"af_kore"     => 5,
		"af_nicole"   => 6,
		"af_nova"     => 7,
		"af_river"    => 8,
		"af_sarah"    => 9,
		"af_sky"      => 10,
		"am_adam"     => 11,
		"am_echo"     => 12,
		"am_eric"     => 13,
		"am_fenrir"   => 14,
		"am_liam"     => 15,
		"am_michael"  => 16,
		"am_onyx"     => 17,
		"am_puck"     => 18,
		"am_santa"    => 19,
		"bf_alice"    => 20,
		"bf_emma"     => 21,
		"bf_isabella" => 22,
		"bf_lily"     => 23,
		"bm_daniel"   => 24,
		"bm_fable"    => 25,
		"bm_george"   => 26,
		"bm_lewis"    => 27,
		_             => 0,
	};

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_tts?.Dispose();
	}
}

