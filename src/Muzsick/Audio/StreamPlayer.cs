// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using Muzsick.Metadata;

namespace Muzsick.Audio;

public class StreamPlayer(ILogger? logger = null) : IDisposable
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
			_mediaPlayer.Volume = 50; // Set volume to 50% (0-100 scale)
			logger?.LogInformation("MediaPlayer created with 50% volume");

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


	public async Task PlayPlaylist(string playlistPath)
	{
		if (!_isInitialized)
		{
			logger?.LogError("Audio engine not initialized");
			StatusChanged?.Invoke("Audio engine not ready");
			return;
		}

		if (_mediaPlayer == null || _libVLC == null)
		{
			logger?.LogError("Audio components not available");
			StatusChanged?.Invoke("Audio components not available");
			return;
		}

		Stop();

		try
		{
			var fileName = Path.GetFileNameWithoutExtension(playlistPath);
			logger?.LogInformation("Loading playlist: {FileName}", fileName);
			StatusChanged?.Invoke($"Loading {fileName}...");

			// Parse the playlist so VLC resolves the stream URLs inside it
			var playlistMedia = new Media(_libVLC, playlistPath);
			await playlistMedia.Parse(MediaParseOptions.ParseLocal | MediaParseOptions.ParseNetwork);
			logger?.LogInformation("Playlist parsed, sub-items: {Count}", playlistMedia.SubItems.Count);

			// Get the first stream URL from the playlist
			var streamMedia = playlistMedia.SubItems.Count > 0 ? playlistMedia.SubItems[0] : null;
			playlistMedia.Dispose();

			if (streamMedia == null)
			{
				logger?.LogError("No streams found in playlist");
				StatusChanged?.Invoke("No streams found in playlist");
				return;
			}

			// Apply ICY and buffering options to the actual stream
			streamMedia.AddOption(":meta-policy=1");
			streamMedia.AddOption(":network-caching=2000");
			streamMedia.AddOption(":icy-metadata=1");

			// Assign and play — OnMediaChanged will hook the metadata events
			_mediaPlayer.Media = streamMedia;
			_currentMedia = streamMedia;

			StatusChanged?.Invoke($"Starting {fileName}...");
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

	public void SetVolume(int volume)
	{
		if (_mediaPlayer == null) return;
		_mediaPlayer.Volume = Math.Clamp(volume, 0, 100);
		logger?.LogDebug("Volume set to {Volume}", volume);
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
