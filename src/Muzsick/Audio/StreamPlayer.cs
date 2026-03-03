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

			// Create LibVLC instance with basic options
			_libVLC = new LibVLC("--no-video", "--intf", "dummy");
			logger?.LogInformation("LibVLC instance created");

			_mediaPlayer = new MediaPlayer(_libVLC);
			logger?.LogInformation("MediaPlayer created");

			// Set up event handlers
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

			// ICY metadata event handler for song changes
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
		logger?.LogInformation("Media changed event fired, Setting up metadata handlers for new media");
		// Set up metadata event handler for the new media
		e.Media.MetaChanged += OnMetaChanged;
		e.Media.ParsedChanged += OnParsedChanged;

		// Try to get metadata immediately
		ExtractAndNotifyTrackInfo(e.Media);
	}

	private void OnMetaChanged(object? sender, MediaMetaChangedEventArgs e)
	{
		logger?.LogDebug("Metadata changed - Type: {MetadataType}", e.MetadataType);

		if (sender is Media media)
		{
			ExtractAndNotifyTrackInfo(media);
		}
	}

	private void OnParsedChanged(object? sender, MediaParsedChangedEventArgs e)
	{
		logger?.LogDebug("Parsed changed - Status: {ParsedStatus}", e.ParsedStatus);

		if (sender is Media media)
		{
			// Try to extract metadata regardless of parse status
			ExtractAndNotifyTrackInfo(media);
		}
	}

	private void ExtractAndNotifyTrackInfo(Media media)
	{
		try
		{
			logger?.LogDebug("ExtractAndNotifyTrackInfo called");

			// Extract ALL possible metadata types
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

			// Skip if we only have the station name as title
			if (title == "radio.truckers.fm" || title.Contains("truckers.fm"))
			{
				logger?.LogDebug("Skipping station name, looking for actual track info");
				title = ""; // Reset to look for real track info
			}

			// ICY metadata often comes in NowPlaying field
			if (!string.IsNullOrEmpty(nowPlaying))
			{
				logger?.LogDebug("Processing NowPlaying metadata: '{NowPlaying}'", nowPlaying);

				// If NowPlaying has actual song info (not just station name)
				if (!nowPlaying.Contains("truckers.fm") && nowPlaying.Trim().Length > 0)
				{
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
						// Use NowPlaying as title if no parsing worked
						title = nowPlaying;
						logger?.LogDebug("Using NowPlaying as title: '{Title}'", title);
					}
				}
			}

			// Also check if Title field has song info (sometimes ICY comes through Title)
			if (!string.IsNullOrEmpty(title) && !title.Contains("truckers.fm"))
			{
				if (string.IsNullOrEmpty(artist) && title.Contains(" - "))
				{
					ParseIcyMetadata(title, out var parsedArtist, out var parsedTitle);
					if (!string.IsNullOrEmpty(parsedArtist))
					{
						artist = parsedArtist;
						title = parsedTitle;
						logger?.LogDebug("Parsed ICY from Title - Artist: '{Artist}', Title: '{Title}'", artist,
							title);
					}
				}
			}

			// Only notify if we have actual song information (not just station info)
			if ((!string.IsNullOrEmpty(title) && !title.Contains("truckers.fm")) || !string.IsNullOrEmpty(artist))
			{
				// Check if this is actually a new track (different from what we last reported)
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

					logger?.LogInformation("NEW TRACK DETECTED - Title: '{Title}', Artist: '{Artist}'",
						trackInfo.Title, trackInfo.Artist);
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

		// Common ICY formats:
		// "Artist - Title"
		// "Artist: Title"
		// "Title by Artist"

		var separators = new[] { " - ", " – ", " — ", ": ", " by " };

		foreach (var separator in separators)
		{
			var parts = icyString.Split([separator], 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2) continue;
			if (separator == " by ")
			{
				// "Title by Artist" format
				title = parts[0].Trim();
				artist = parts[1].Trim();
			}
			else
			{
				// "Artist - Title" format
				artist = parts[0].Trim();
				title = parts[1].Trim();
			}

			return;
		}

		// If no separator found, treat the whole string as title
		title = icyString.Trim();
	}

	public async Task PlayPlaylist(string playlistPath)
	{
		try
		{
			logger?.LogInformation("PlayPlaylist called with path: {PlaylistPath}", playlistPath);

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

				// Create media from playlist and await its parsing with a 5-second timeout
				_currentMedia = new Media(_libVLC, playlistPath);

				var parseResult = await _currentMedia.Parse(MediaParseOptions.ParseNetwork, timeout: 5000);

				if (parseResult != MediaParsedStatus.Done)
				{
					logger?.LogError("Playlist parsing failed or timed out: {Result}", parseResult);
					StatusChanged?.Invoke("Failed to load playlist");
					return;
				}

				logger?.LogInformation("Playlist parsed successfully");

				// Get the first playable item from the parsed playlist
				var mediaList = _currentMedia.SubItems;
				if (mediaList.Count > 0)
				{
					// Guard: index access on MediaList can return null
					var firstItem = mediaList[0];
					if (firstItem == null)
					{
						logger?.LogError("First playlist item was null");
						StatusChanged?.Invoke("No streams found in playlist");
						return;
					}

					// Set up options on the actual stream media
					firstItem.AddOption(":meta-policy=1");
					firstItem.AddOption(":network-caching=2000");
					firstItem.AddOption(":icy-metadata=1");

					logger?.LogDebug("Setting up metadata event handlers on stream media");
					firstItem.MetaChanged += OnMetaChanged;
					firstItem.ParsedChanged += OnParsedChanged;

					logger?.LogDebug("Setting stream media to player and starting playback");
					_mediaPlayer.Media = firstItem;
					_currentMedia = firstItem; // Update reference to the actual stream

					StatusChanged?.Invoke($"Starting {fileName}...");

					var started = _mediaPlayer.Play();
					logger?.LogInformation("MediaPlayer.Play() returned: {Result}", started);

					if (!started)
					{
						logger?.LogError("Failed to start playback");
						StatusChanged?.Invoke("Failed to start playback");
					}
				}
				else
				{
					logger?.LogError("No playable items found in playlist");
					StatusChanged?.Invoke("No streams found in playlist");
				}
			}
			catch (Exception ex)
			{
				logger?.LogError(ex, "Exception in PlayPlaylist");
				StatusChanged?.Invoke("Playback error occurred");
			}
		}
		catch (Exception e)
		{
			logger?.LogError(e, "Unexpected error in PlayPlaylist");
		}
	}

	private void StartMetadataPolling()
	{
		logger?.LogDebug("Starting metadata polling timer");
		StopMetadataPolling(); // Stop any existing timer

		// Poll for metadata every 2 seconds
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
		{
			_mediaPlayer.Stop();
		}

		if (_currentMedia == null) return;
		// Clean up event handlers
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
