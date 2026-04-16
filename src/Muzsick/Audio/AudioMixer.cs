// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Silk.NET.OpenAL;

namespace Muzsick.Audio;

public class AudioMixer : IDisposable
{
	private readonly ILogger? _logger;
	private readonly AL _al;
	private readonly ALContext _alc;

	// Stored as nint to avoid unsafe field declarations at class scope.
	private nint _device;
	private nint _context;

	private uint _ttsSource;
	private readonly object _alLock = new();

	private float _djGain = 1.0f;

	public AudioMixer(ILogger? logger = null)
	{
		_logger = logger;
		// Silk.NET.OpenAL is managed bindings only — no native library is bundled by NuGet.
		// On Windows, AL.GetApi(soft:true) loads soft_oal.dll — the official OpenAL-Soft
		// Windows binary name. That file is committed to Native/win-x64/ in the repo and
		// copied to the output directory via a <Content> item in the csproj.
		// On Linux/macOS, libopenal is a standard system library; soft:false finds it directly.
		var soft = OperatingSystem.IsWindows();
		_al = AL.GetApi(soft);
		_alc = ALContext.GetApi(soft);

		Initialize();
	}

	private unsafe void Initialize()
	{
		var device = _alc.OpenDevice(null);
		if (device == null)
		{
			_logger?.LogError("OpenAL: failed to open default device");
			return;
		}

		_device = (nint)device;

		var context = _alc.CreateContext(device, null);
		if (context == null)
		{
			_logger?.LogError("OpenAL: failed to create context");
			return;
		}

		_context = (nint)context;
		_alc.MakeContextCurrent(context);

		_ttsSource = _al.GenSource();
		_al.SetSourceProperty(_ttsSource, SourceBoolean.Looping, false);
		_al.SetSourceProperty(_ttsSource, SourceFloat.Gain, _djGain);

		_logger?.LogInformation("OpenAL initialized — device opened");
	}

	/// <summary>
	/// Plays a voiceover WAV through the TTS source.
	/// </summary>
	public async Task PlayVoiceoverAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
	{
		if (wavBytes is not { Length: > 0 }) return;

		if (!ParseWav(wavBytes, out var pcmOffset, out var pcmLength, out var wavSampleRate, out var wavChannels, out var audioFormat))
		{
			_logger?.LogWarning("PlayVoiceoverAsync: could not parse WAV header");
			return;
		}

		// OpenAL-Soft via Silk.NET only exposes int16 buffer formats.
		// Kokoro outputs 32-bit IEEE float (audioFormat == 3), so convert to int16.
		byte[] pcmBytes;
		if (audioFormat == 3)
		{
			pcmBytes = ConvertFloat32ToInt16(wavBytes, pcmOffset, pcmLength);
		}
		else
		{
			pcmBytes = new byte[pcmLength];
			Array.Copy(wavBytes, pcmOffset, pcmBytes, 0, pcmLength);
		}

		var ttsBuffer = _al.GenBuffer();

		try
		{
			var format = wavChannels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;

			unsafe
			{
				fixed (byte* ptr = pcmBytes)
				{
					lock (_alLock)
					{
						_al.BufferData(ttsBuffer, format, ptr, pcmBytes.Length, wavSampleRate);
						_al.SetSourceProperty(_ttsSource, SourceInteger.Buffer, (int)ttsBuffer);
					}
				}
			}

			lock (_alLock)
			{
				_al.SourcePlay(_ttsSource);
			}

			_logger?.LogDebug("PlayVoiceoverAsync: TTS source started");

			// Poll until playback finishes or is cancelled.
			while (!cancellationToken.IsCancellationRequested)
			{
				await Task.Delay(50, cancellationToken).ContinueWith(_ => { });

				lock (_alLock)
				{
					_al.GetSourceProperty(_ttsSource, GetSourceInteger.SourceState, out var state);
					if ((SourceState)state != SourceState.Playing)
						break;
				}
			}

			if (cancellationToken.IsCancellationRequested)
			{
				lock (_alLock)
				{
					_al.SourceStop(_ttsSource);
				}

				_logger?.LogDebug("PlayVoiceoverAsync: cancelled — TTS stopped");
			}
			else
			{
				_logger?.LogDebug("PlayVoiceoverAsync: TTS playback complete");
			}
		}
		finally
		{
			// Detach the buffer from the source before deleting it.
			lock (_alLock)
			{
				_al.SetSourceProperty(_ttsSource, SourceInteger.Buffer, 0);
				_al.DeleteBuffer(ttsBuffer);
			}
		}
	}

	/// <summary>
	/// Sets the DJ voiceover source gain. <paramref name="volume"/> is 0–100.
	/// </summary>
	public void SetDjVolume(int volume)
	{
		_djGain = Math.Clamp(volume, 0, 100) / 100f;

		lock (_alLock)
		{
			_al.SetSourceProperty(_ttsSource, SourceFloat.Gain, _djGain);
		}

		_logger?.LogDebug("OpenAL: DJ gain set to {Gain:F2}", _djGain);
	}

	// Parses a standard PCM WAV header.
	// Returns true and fills the out parameters on success; false if the header is invalid.
	// audioFormat: 1 = PCM int16, 3 = IEEE float32
	private static bool ParseWav(
		byte[] data,
		out int pcmOffset,
		out int pcmLength,
		out int sampleRate,
		out int channels,
		out int audioFormat)
	{
		pcmOffset = 0;
		pcmLength = 0;
		sampleRate = 0;
		channels = 0;
		audioFormat = 1;

		// Minimum WAV size: 44 bytes
		if (data.Length < 44) return false;

		// RIFF header
		if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
		if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return false;

		audioFormat = BitConverter.ToInt16(data, 20);
		channels = BitConverter.ToInt16(data, 22);
		sampleRate = BitConverter.ToInt32(data, 24);

		// Walk chunks to find "data"
		var pos = 12;
		while (pos + 8 <= data.Length)
		{
			var chunkId = Encoding.ASCII.GetString(data, pos, 4);
			var chunkSize = BitConverter.ToInt32(data, pos + 4);

			if (chunkId == "data")
			{
				pcmOffset = pos + 8;
				pcmLength = Math.Min(chunkSize, data.Length - pcmOffset);
				return pcmLength > 0;
			}

			pos += 8 + chunkSize;
		}

		return false;
	}

	// Converts IEEE float32 PCM bytes to int16 PCM bytes.
	private static byte[] ConvertFloat32ToInt16(byte[] src, int offset, int length)
	{
		var sampleCount = length / sizeof(float);
		var dst = new byte[sampleCount * sizeof(short)];

		for (var i = 0; i < sampleCount; i++)
		{
			var f = BitConverter.ToSingle(src, offset + i * sizeof(float));
			var clamped = Math.Clamp(f, -1.0f, 1.0f);
			var s = (short)(clamped * short.MaxValue);
			dst[i * 2] = (byte)(s & 0xFF);
			dst[i * 2 + 1] = (byte)(s >> 8);
		}

		return dst;
	}

	public unsafe void Dispose()
	{
		lock (_alLock)
		{
			_al.SourceStop(_ttsSource);
			_al.DeleteSource(_ttsSource);
		}

		if (_context != 0)
		{
			_alc.MakeContextCurrent((Context*)0);
			_alc.DestroyContext((Context*)_context);
			_context = 0;
		}

		if (_device != 0)
		{
			_alc.CloseDevice((Device*)_device);
			_device = 0;
		}

		_al.Dispose();
		_alc.Dispose();

		_logger?.LogInformation("OpenAL disposed");
	}
}
