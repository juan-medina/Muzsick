// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using Muzsick.Metadata;

namespace Muzsick.Audio;

public class StreamPlayer(AudioMixer audioMixer, ILogger? logger = null) : IDisposable
{
	// ReSharper disable once InconsistentNaming
	private LibVLC? _libVLC;
	private MediaPlayer? _mediaPlayer;
	private Media? _currentMedia;
	private bool _isInitialized;
	private Timer? _metadataTimer;
	private string _lastKnownTitle = "";
	private string _lastKnownArtist = "";
	private readonly IcyMetadataParser _icyParser = new(logger);

	// Keep delegates alive for the lifetime of the player so the GC does not collect them
	// while LibVLC is still invoking them.
	private MediaPlayer.LibVLCAudioPlayCb? _playCb;
	private MediaPlayer.LibVLCAudioFlushCb? _flushCb;
	private MediaPlayer.LibVLCAudioDrainCb? _drainCb;

	// Fixed audio format negotiated with LibVLC via SetAudioFormat.
	private const uint _sampleRate = 44100;
	private const uint _channels = 2;

	public event Action<string>? StatusChanged;
	public event Action<TrackInfo>? TrackChanged;

	public void Initialize()
	{
		if (_isInitialized) return;

		try
		{
			logger?.LogInformation("Starting LibVLC initialization...");
			StatusChanged?.Invoke("Initializing audio engine...");

			Core.Initialize();
			logger?.LogInformation("LibVLC core initialized");

			_libVLC = new LibVLC("--no-video", "--intf", "dummy");
			logger?.LogInformation("LibVLC instance created");

			_mediaPlayer = new MediaPlayer(_libVLC);
			logger?.LogInformation("MediaPlayer created");

			// Wire up audio callbacks — VLC delivers decoded PCM here.
			// SetAudioFormat tells VLC to decode as signed 16-bit native-endian stereo at 44100 Hz
			// so we always know the exact format arriving in OnAudioPlay.
			// Delegates are stored in fields to prevent GC collection.
			_mediaPlayer.SetAudioFormat("S16N", _sampleRate, _channels);

			_playCb = OnAudioPlay;
			_flushCb = OnAudioFlush;
			_drainCb = OnAudioDrain;
			_mediaPlayer.SetAudioCallbacks(_playCb, null, null, _flushCb, _drainCb);

			_mediaPlayer.Playing += (_, _) =>
			{
				logger?.LogInformation("Stream started playing");
				StatusChanged?.Invoke("Connected to stream");
				StartMetadataPolling();
			};
			_mediaPlayer.Stopped += (_, _) =>
			{
				logger?.LogInformation("Stream stopped");
				StatusChanged?.Invoke("Stopped");
				StopMetadataPolling();
			};
			_mediaPlayer.EncounteredError += (_, _) =>
			{
				logger?.LogWarning("Stream encountered error");
				StatusChanged?.Invoke("Connection error");
				StopMetadataPolling();
			};
			_mediaPlayer.EndReached += (_, _) =>
			{
				logger?.LogInformation("Stream end reached");
				StatusChanged?.Invoke("Stream ended");
				StopMetadataPolling();
			};

			// When VLC advances to the actual stream media, hook metadata events on it
			_mediaPlayer.MediaChanged += OnMediaChanged;

			_isInitialized = true;
			logger?.LogInformation("Audio engine initialized successfully");
			StatusChanged?.Invoke("Audio engine ready");
		}
		catch (Exception ex)
		{
			logger?.LogError(ex, "Failed to initialize audio engine");
			StatusChanged?.Invoke("Audio engine failed to initialize");
		}
	}

	// Called from the LibVLC decode thread — copy samples into AudioMixer.
	private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
	{
		// count = sample frames; S16N stereo = 2 ch × 2 bytes per frame
		var byteCount = (int)(count * _channels * 2);
		audioMixer.EnqueuePcm(samples, byteCount, (int)_sampleRate, (int)_channels);
	}

	private void OnAudioFlush(IntPtr data, long pts)
	{
		logger?.LogDebug("LibVLC flush callback");
		audioMixer.Flush();
	}

	private void OnAudioDrain(IntPtr data)
	{
		logger?.LogDebug("LibVLC drain callback");
	}

	private void OnMediaChanged(object? sender, MediaPlayerMediaChangedEventArgs e)
	{
		logger?.LogInformation("Media changed — hooking metadata events on new media");

		// Unhook previous media if any
		if (_currentMedia != null)
		{
			_currentMedia.MetaChanged -= OnMetaChanged;
			_currentMedia.ParsedChanged -= OnParsedChanged;
		}

		_currentMedia = e.Media;
		_currentMedia.MetaChanged += OnMetaChanged;
		_currentMedia.ParsedChanged += OnParsedChanged;
		ExtractAndNotifyTrackInfo(_currentMedia);
	}

	private void OnMetaChanged(object? sender, MediaMetaChangedEventArgs e)
	{
		logger?.LogDebug("Metadata changed - Type: {MetadataType}", e.MetadataType);
		if (sender is Media media)
			ExtractAndNotifyTrackInfo(media);
	}

	private void OnParsedChanged(object? sender, MediaParsedChangedEventArgs e)
	{
		logger?.LogDebug("Parsed changed - Status: {ParsedStatus}", e.ParsedStatus);
		if (sender is Media media)
			ExtractAndNotifyTrackInfo(media);
	}

	private void ExtractAndNotifyTrackInfo(Media media)
	{
		try
		{
			logger?.LogDebug("ExtractAndNotifyTrackInfo called");

			var trackInfo = _icyParser.ExtractTrackInfo(media);
			if (trackInfo == null)
			{
				logger?.LogDebug("No meaningful track metadata found, keeping current display");
				return;
			}

			if (trackInfo.Title == _lastKnownTitle && trackInfo.Artist == _lastKnownArtist)
			{
				logger?.LogDebug("Same track as before, not notifying");
				return;
			}

			_lastKnownTitle = trackInfo.Title;
			_lastKnownArtist = trackInfo.Artist;

			logger?.LogInformation("NEW TRACK DETECTED - Title: '{Title}', Artist: '{Artist}'", trackInfo.Title,
				trackInfo.Artist);
			TrackChanged?.Invoke(trackInfo);
		}
		catch (Exception ex)
		{
			logger?.LogError(ex, "Error reading metadata");
		}
	}

	public Task PlayPlaylist(string streamUrl)
	{
		if (!_isInitialized)
		{
			logger?.LogError("Audio engine not initialized");
			StatusChanged?.Invoke("Audio engine not ready");
			return Task.CompletedTask;
		}

		if (_mediaPlayer == null || _libVLC == null)
		{
			logger?.LogError("Audio components not available");
			StatusChanged?.Invoke("Audio components not available");
			return Task.CompletedTask;
		}

		Stop();

		try
		{
			var displayName = Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri)
				? uri.Host
				: streamUrl;

			logger?.LogInformation("Loading stream: {DisplayName}", displayName);
			StatusChanged?.Invoke($"Loading {displayName}...");

			var streamMedia = new Media(_libVLC, new Uri(streamUrl));
			streamMedia.AddOption(":meta-policy=1");
			streamMedia.AddOption(":network-caching=2000");
			streamMedia.AddOption(":icy-metadata=1");

			_mediaPlayer.Media = streamMedia;
			_currentMedia = streamMedia;

			StatusChanged?.Invoke($"Starting {displayName}...");
			var started = _mediaPlayer.Play();
			logger?.LogInformation("MediaPlayer.Play() returned: {Result}", started);

			if (!started)
			{
				logger?.LogError("Failed to start playback");
				StatusChanged?.Invoke("Failed to start playback");
			}
		}
		catch (Exception ex)
		{
			logger?.LogError(ex, "Exception in PlayPlaylist");
			StatusChanged?.Invoke("Playback error occurred");
		}

		return Task.CompletedTask;
	}

	private void StartMetadataPolling()
	{
		logger?.LogDebug("Starting metadata polling timer");
		StopMetadataPolling();
		_metadataTimer = new Timer(PollMetadata, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
	}

	private void StopMetadataPolling()
	{
		logger?.LogDebug("Stopping metadata polling timer");
		_metadataTimer?.Dispose();
		_metadataTimer = null;
	}

	private void PollMetadata(object? state)
	{
		if (_currentMedia == null) return;
		logger?.LogDebug("Polling metadata...");
		ExtractAndNotifyTrackInfo(_currentMedia);
	}

	public void Stop()
	{
		logger?.LogInformation("Stop called");
		StopMetadataPolling();

		if (_mediaPlayer is { IsPlaying: true })
			_mediaPlayer.Stop();

		if (_currentMedia == null) return;
		_currentMedia.MetaChanged -= OnMetaChanged;
		_currentMedia.ParsedChanged -= OnParsedChanged;
		_currentMedia.Dispose();
		_currentMedia = null;
	}

	public void Dispose()
	{
		logger?.LogInformation("Dispose called");
		StopMetadataPolling();
		Stop();
		_mediaPlayer?.Dispose();
		_libVLC?.Dispose();
		_isInitialized = false;
	}
}
