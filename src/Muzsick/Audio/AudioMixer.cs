// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Silk.NET.OpenAL;

namespace Muzsick.Audio;

/// <summary>
/// Owns the OpenAL device, context, and streaming radio source.
/// LibVLC pushes decoded PCM via <see cref="EnqueuePcm"/>; this class is
/// the single audio output point for the application.
/// </summary>
public class AudioMixer : IDisposable
{
	private const int _bufferCount = 64;
	private const float _duckGain = 0.20f;
	private const int _duckDownMs = 500;
	private const int _duckUpMs = 800;

	private readonly ILogger? _logger;
	private readonly AL _al;
	private readonly ALContext _alc;

	// Stored as nint to avoid unsafe field declarations at class scope.
	private nint _device;
	private nint _context;

	private uint _radioSource;
	private uint _ttsSource;
	private readonly uint[] _buffers = new uint[_bufferCount];
	private readonly ConcurrentQueue<uint> _freeBuffers = new();
	private readonly object _alLock = new();

	private readonly Thread _recyclerThread;
	private volatile bool _disposing;

	private int _sampleRate = 44100;
	private int _channels = 2;
	private float _gain = 0.5f;

	private DuckingController? _ducking;

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

		_recyclerThread = new Thread(RecycleLoop) { IsBackground = true, Name = "AudioMixer-Recycler" };
		_recyclerThread.Start();
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

		_radioSource = _al.GenSource();
		_al.SetSourceProperty(_radioSource, SourceBoolean.Looping, false);
		_al.SetSourceProperty(_radioSource, SourceFloat.Gain, _gain);

		_ttsSource = _al.GenSource();
		_al.SetSourceProperty(_ttsSource, SourceBoolean.Looping, false);
		_al.SetSourceProperty(_ttsSource, SourceFloat.Gain, 1.0f);

		_ducking = new DuckingController(_al, _alLock, _logger);

		var bufs = _al.GenBuffers(_bufferCount);
		for (var i = 0; i < _bufferCount; i++)
		{
			_buffers[i] = bufs[i];
			_freeBuffers.Enqueue(bufs[i]);
		}

		_logger?.LogInformation("OpenAL initialized — device opened, {Count} buffers ready", _bufferCount);
	}

	/// <summary>
	/// Called from the LibVLC audio callback thread with raw interleaved S16 PCM.
	/// Thread-safe: acquires <see cref="_alLock"/> for OpenAL calls.
	/// </summary>
	public unsafe void EnqueuePcm(IntPtr samples, int byteCount, int sampleRate, int channels)
	{
		if (_disposing) return;

		if (!_freeBuffers.TryDequeue(out var buffer))
		{
			_logger?.LogWarning("OpenAL: no free buffer available — dropping PCM chunk ({Bytes} bytes)", byteCount);
			return;
		}

		// Update negotiated format if it changed
		if (sampleRate != _sampleRate || channels != _channels)
		{
			_sampleRate = sampleRate;
			_channels = channels;
			_logger?.LogDebug("OpenAL: format updated — {Rate} Hz, {Ch} ch", sampleRate, channels);
		}

		var format = channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;

		lock (_alLock)
		{
			_al.BufferData(buffer, format, (void*)samples, byteCount, sampleRate);
			_al.SourceQueueBuffers(_radioSource, [buffer]);

			_al.GetSourceProperty(_radioSource, GetSourceInteger.SourceState, out var state);
			if ((SourceState)state == SourceState.Playing) return;
			_al.SourcePlay(_radioSource);
			_logger?.LogDebug("OpenAL: source started playing");
		}
	}

	/// <summary>
	/// Plays a voiceover WAV through the TTS source with radio ducking.
	/// Safe to cancel: on cancellation the TTS source is stopped and the radio
	/// gain is restored immediately.
	/// </summary>
	public async Task PlayVoiceoverAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
	{
		if (_disposing || _ducking == null) return;
		if (wavBytes is not { Length: > 0 }) return;

		if (!ParseWav(wavBytes, out var pcmOffset, out var pcmLength, out var wavSampleRate, out var wavChannels))
		{
			_logger?.LogWarning("PlayVoiceoverAsync: could not parse WAV header");
			return;
		}

		// Allocate a one-shot buffer for the TTS audio.
		var ttsBuffer = _al.GenBuffer();

		try
		{
			var format = wavChannels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;

			unsafe
			{
				fixed (byte* ptr = wavBytes)
				{
					lock (_alLock)
					{
						_al.BufferData(ttsBuffer, format, ptr + pcmOffset, pcmLength, wavSampleRate);
						_al.SetSourceProperty(_ttsSource, SourceInteger.Buffer, (int)ttsBuffer);
					}
				}
			}

			// Duck the radio stream down.
			await _ducking.FadeAsync(_radioSource, _gain, _gain * _duckGain, _duckDownMs, cancellationToken);

			if (cancellationToken.IsCancellationRequested)
			{
				RestoreRadioGain();
				return;
			}

			// Start TTS playback.
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

				RestoreRadioGain();
				_logger?.LogDebug("PlayVoiceoverAsync: cancelled — TTS stopped, radio gain restored");
				return;
			}

			_logger?.LogDebug("PlayVoiceoverAsync: TTS playback complete, fading radio back up");

			// Restore radio gain with a smooth fade.
			await _ducking.FadeAsync(_radioSource, _gain * _duckGain, _gain, _duckUpMs, cancellationToken);

			if (cancellationToken.IsCancellationRequested)
				RestoreRadioGain();
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

	// Immediately snaps the radio source back to the user-configured gain.
	// Used in cancellation paths where a smooth fade is not appropriate.
	private void RestoreRadioGain()
	{
		lock (_alLock)
		{
			_al.SetSourceProperty(_radioSource, SourceFloat.Gain, _gain);
		}

		_logger?.LogDebug("AudioMixer: radio gain restored to {Gain:F2}", _gain);
	}

	// Parses a standard PCM WAV header.
	// Returns true and fills the out parameters on success; false if the header is invalid.
	private static bool ParseWav(
		byte[] data,
		out int pcmOffset,
		out int pcmLength,
		out int sampleRate,
		out int channels)
	{
		pcmOffset = 0;
		pcmLength = 0;
		sampleRate = 0;
		channels = 0;

		// Minimum WAV size: 44 bytes
		if (data.Length < 44) return false;

		// RIFF header
		if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
		if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return false;

		channels = BitConverter.ToInt16(data, 22);
		sampleRate = BitConverter.ToInt32(data, 24);

		// Walk chunks to find "data"
		var pos = 12;
		while (pos + 8 <= data.Length)
		{
			var chunkId = System.Text.Encoding.ASCII.GetString(data, pos, 4);
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

	/// <summary>
	/// Stops playback and returns all queued buffers to the free pool.
	/// Called when the stream is stopped or flushed.
	/// </summary>
	public void Flush()
	{
		lock (_alLock)
		{
			_al.SourceStop(_radioSource);
			UnqueueProcessed();
		}

		_logger?.LogDebug("OpenAL: flushed");
	}

	/// <summary>
	/// Sets the radio stream gain. <paramref name="volume"/> is 0–100.
	/// </summary>
	public void SetVolume(int volume)
	{
		_gain = Math.Clamp(volume, 0, 100) / 100f;

		lock (_alLock)
		{
			_al.SetSourceProperty(_radioSource, SourceFloat.Gain, _gain);
		}

		_logger?.LogDebug("OpenAL: gain set to {Gain:F2}", _gain);
	}

	// Recycles processed buffers back to the free pool every 50 ms.
	private void RecycleLoop()
	{
		while (!_disposing)
		{
			Thread.Sleep(50);
			if (_disposing) break;

			lock (_alLock)
			{
				UnqueueProcessed();
			}
		}
	}

	// Must be called with _alLock held.
	private void UnqueueProcessed()
	{
		_al.GetSourceProperty(_radioSource, GetSourceInteger.BuffersProcessed, out var processed);
		if (processed <= 0) return;

		// Build an array of the right size and unqueue into it.
		var toRelease = new uint[processed];
		_al.SourceUnqueueBuffers(_radioSource, toRelease);
		foreach (var b in toRelease)
			_freeBuffers.Enqueue(b);
	}

	public unsafe void Dispose()
	{
		_disposing = true;
		_recyclerThread.Join(500);

		lock (_alLock)
		{
			_al.SourceStop(_radioSource);
			_al.SourceStop(_ttsSource);
			UnqueueProcessed();
			_al.DeleteSource(_radioSource);
			_al.DeleteSource(_ttsSource);
			_al.DeleteBuffers(_buffers);
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
