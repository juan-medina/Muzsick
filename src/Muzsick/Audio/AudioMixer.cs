// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Threading;
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

	private readonly ILogger? _logger;
	private readonly AL _al;
	private readonly ALContext _alc;

	// Stored as nint to avoid unsafe field declarations at class scope.
	private nint _device;
	private nint _context;

	private uint _radioSource;
	private readonly uint[] _buffers = new uint[_bufferCount];
	private readonly ConcurrentQueue<uint> _freeBuffers = new();
	private readonly object _alLock = new();

	private readonly Thread _recyclerThread;
	private volatile bool _disposing;

	private int _sampleRate = 44100;
	private int _channels = 2;
	private float _gain = 0.5f;

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
			UnqueueProcessed();
			_al.DeleteSource(_radioSource);
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
