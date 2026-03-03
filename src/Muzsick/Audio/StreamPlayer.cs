// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace Muzsick.Audio;

public class TrackInfo
{
	public string Title { get; init; } = "";
	public string Artist { get; init; } = "";
	public string Album { get; init; } = "";
}

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

			var title = media.Meta(MetadataType.Title) ?? "";
			var artist = media.Meta(MetadataType.Artist) ?? "";
			var album = media.Meta(MetadataType.Album) ?? "";
			var nowPlaying = media.Meta(MetadataType.NowPlaying) ?? "";
			var description = media.Meta(MetadataType.Description) ?? "";
			var genre = media.Meta(MetadataType.Genre) ?? "";

			logger?.LogDebug("Raw metadata - Title: '{Title}', Artist: '{Artist}', Album: '{Album}'", title, artist,
				album);
			logger?.LogDebug(
				"Raw metadata - NowPlaying: '{NowPlaying}', Description: '{Description}', Genre: '{Genre}'", nowPlaying,
				description, genre);

			if (title.Contains("truckers.fm"))
			{
				logger?.LogDebug("Skipping station name, looking for actual track info");
				title = "";
			}

			if (!string.IsNullOrEmpty(nowPlaying) && !nowPlaying.Contains("truckers.fm"))
			{
				logger?.LogDebug("Processing NowPlaying metadata: '{NowPlaying}'", nowPlaying);
				ParseIcyMetadata(nowPlaying, out var parsedArtist, out var parsedTitle);

				if (!string.IsNullOrEmpty(parsedArtist) || !string.IsNullOrEmpty(parsedTitle))
				{
					artist = parsedArtist;
					title = parsedTitle;
					logger?.LogDebug("Parsed ICY from NowPlaying - Artist: '{Artist}', Title: '{Title}'", artist,
						title);
				}
				else if (string.IsNullOrEmpty(title))
				{
					title = nowPlaying;
					logger?.LogDebug("Using NowPlaying as title: '{Title}'", title);
				}
			}

			if (!string.IsNullOrEmpty(title) && !title.Contains("truckers.fm") && string.IsNullOrEmpty(artist) &&
			    title.Contains(" - "))
			{
				ParseIcyMetadata(title, out var parsedArtist, out var parsedTitle);
				if (!string.IsNullOrEmpty(parsedArtist))
				{
					artist = parsedArtist;
					title = parsedTitle;
					logger?.LogDebug("Parsed ICY from Title - Artist: '{Artist}', Title: '{Title}'", artist, title);
				}
			}

			if ((!string.IsNullOrEmpty(title) && !title.Contains("truckers.fm")) || !string.IsNullOrEmpty(artist))
			{
				if (title != _lastKnownTitle || artist != _lastKnownArtist)
				{
					_lastKnownTitle = title;
					_lastKnownArtist = artist;

					var trackInfo = new TrackInfo
					{
						Title = title,
						Artist = string.IsNullOrEmpty(artist) ? "Unknown Artist" : artist,
						Album = album
					};

					logger?.LogInformation("NEW TRACK DETECTED - Title: '{Title}', Artist: '{Artist}'", trackInfo.Title,
						trackInfo.Artist);
					TrackChanged?.Invoke(trackInfo);
				}
				else
				{
					logger?.LogDebug("Same track as before, not notifying");
				}
			}
			else
			{
				logger?.LogDebug("No meaningful track metadata found, keeping current display");
			}
		}
		catch (Exception ex)
		{
			logger?.LogError(ex, "Error reading metadata");
		}
	}

	private void ParseIcyMetadata(string icyString, out string artist, out string title)
	{
		artist = "";
		title = "";

		if (string.IsNullOrEmpty(icyString)) return;

		// Common ICY formats: "Artist - Title", "Artist: Title", "Title by Artist"
		var separators = new[] { " - ", " – ", " — ", ": ", " by " };

		foreach (var separator in separators)
		{
			var parts = icyString.Split([separator], 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2) continue;

			if (separator == " by ")
			{
				title = parts[0].Trim();
				artist = parts[1].Trim();
			}
			else
			{
				artist = parts[0].Trim();
				title = parts[1].Trim();
			}

			return;
		}

		// No separator found — treat the whole string as title
		title = icyString.Trim();
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
