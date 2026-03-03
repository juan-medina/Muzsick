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
	public string Title { get; set; } = "";
	public string Artist { get; set; } = "";
	public string Album { get; set; } = "";
}

public class StreamPlayer : IDisposable
{
	private LibVLC? _libVLC;
	private MediaPlayer? _mediaPlayer;
	private Media? _currentMedia;
	private bool _isInitialized;
	private Timer? _metadataTimer;
	private string _lastKnownTitle = "";
	private string _lastKnownArtist = "";
	private readonly ILogger? _logger;

	public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;

	public event Action<string>? StatusChanged;
	public event Action<TrackInfo>? TrackChanged;

	public StreamPlayer(ILogger? logger = null)
	{
		_logger = logger;
	}

	public void Initialize()
	{
		if (_isInitialized) return;

		try
		{
			_logger?.LogInformation("Starting LibVLC initialization...");
			StatusChanged?.Invoke("Initializing audio engine...");

			Core.Initialize();
			_logger?.LogInformation("LibVLC core initialized");

			// Create LibVLC instance with basic options
			_libVLC = new LibVLC("--no-video", "--intf", "dummy");
			_logger?.LogInformation("LibVLC instance created");

			_mediaPlayer = new MediaPlayer(_libVLC);
			_logger?.LogInformation("MediaPlayer created");

			// Set up event handlers
			_mediaPlayer.Playing += (sender, e) => {
				_logger?.LogInformation("Stream started playing");
				StatusChanged?.Invoke("Connected to stream");
				StartMetadataPolling();
			};
			_mediaPlayer.Stopped += (sender, e) => {
				_logger?.LogInformation("Stream stopped");
				StatusChanged?.Invoke("Stopped");
				StopMetadataPolling();
			};
			_mediaPlayer.EncounteredError += (sender, e) => {
				_logger?.LogWarning("Stream encountered error");
				StatusChanged?.Invoke("Connection error");
				StopMetadataPolling();
			};
			_mediaPlayer.EndReached += (sender, e) => {
				_logger?.LogInformation("Stream end reached");
				StatusChanged?.Invoke("Stream ended");
				StopMetadataPolling();
			};

			// ICY metadata event handler for song changes
			_mediaPlayer.MediaChanged += OnMediaChanged;

			_isInitialized = true;
			_logger?.LogInformation("Audio engine initialized successfully");
			StatusChanged?.Invoke("Audio engine ready");
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Failed to initialize audio engine");
			StatusChanged?.Invoke("Audio engine failed to initialize");
		}
	}

	private void OnMediaChanged(object? sender, MediaPlayerMediaChangedEventArgs e)
	{
		_logger?.LogInformation("Media changed event fired");

		if (e.Media != null)
		{
			_logger?.LogInformation("Setting up metadata handlers for new media");
			// Set up metadata event handler for the new media
			e.Media.MetaChanged += OnMetaChanged;
			e.Media.ParsedChanged += OnParsedChanged;

			// Try to get metadata immediately
			ExtractAndNotifyTrackInfo(e.Media);
		}
	}

	private void OnMetaChanged(object? sender, MediaMetaChangedEventArgs e)
	{
		_logger?.LogDebug("Metadata changed - Type: {MetadataType}", e.MetadataType);

		if (sender is Media media)
		{
			ExtractAndNotifyTrackInfo(media);
		}
	}

	private void OnParsedChanged(object? sender, MediaParsedChangedEventArgs e)
	{
		_logger?.LogDebug("Parsed changed - Status: {ParsedStatus}", e.ParsedStatus);

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
			_logger?.LogDebug("ExtractAndNotifyTrackInfo called");

			// Extract ALL possible metadata types
			var title = media.Meta(MetadataType.Title) ?? "";
			var artist = media.Meta(MetadataType.Artist) ?? "";
			var album = media.Meta(MetadataType.Album) ?? "";
			var nowPlaying = media.Meta(MetadataType.NowPlaying) ?? "";
			var description = media.Meta(MetadataType.Description) ?? "";
			var genre = media.Meta(MetadataType.Genre) ?? "";

			_logger?.LogDebug("Raw metadata - Title: '{Title}', Artist: '{Artist}', Album: '{Album}'", title, artist, album);
			_logger?.LogDebug("Raw metadata - NowPlaying: '{NowPlaying}', Description: '{Description}', Genre: '{Genre}'", nowPlaying, description, genre);

			// Skip if we only have the station name as title
			if (title == "radio.truckers.fm" || title.Contains("truckers.fm"))
			{
				_logger?.LogDebug("Skipping station name, looking for actual track info");
				title = ""; // Reset to look for real track info
			}

			// ICY metadata often comes in NowPlaying field
			if (!string.IsNullOrEmpty(nowPlaying))
			{
				_logger?.LogDebug("Processing NowPlaying metadata: '{NowPlaying}'", nowPlaying);

				// If NowPlaying has actual song info (not just station name)
				if (!nowPlaying.Contains("truckers.fm") && nowPlaying.Trim().Length > 0)
				{
					ParseIcyMetadata(nowPlaying, out var parsedArtist, out var parsedTitle);

					if (!string.IsNullOrEmpty(parsedArtist) || !string.IsNullOrEmpty(parsedTitle))
					{
						artist = parsedArtist;
						title = parsedTitle;
						_logger?.LogDebug("Parsed ICY from NowPlaying - Artist: '{Artist}', Title: '{Title}'", artist, title);
					}
					else if (string.IsNullOrEmpty(title))
					{
						// Use NowPlaying as title if no parsing worked
						title = nowPlaying;
						_logger?.LogDebug("Using NowPlaying as title: '{Title}'", title);
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
						_logger?.LogDebug("Parsed ICY from Title - Artist: '{Artist}', Title: '{Title}'", artist, title);
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

					_logger?.LogInformation("NEW TRACK DETECTED - Title: '{Title}', Artist: '{Artist}'", trackInfo.Title, trackInfo.Artist);
					TrackChanged?.Invoke(trackInfo);
				}
				else
				{
					_logger?.LogDebug("Same track as before, not notifying");
				}
			}
			else
			{
				_logger?.LogDebug("No meaningful track metadata found, keeping current display");
			}
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Error reading metadata");
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
			var parts = icyString.Split(new[] { separator }, 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 2)
			{
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
		}

		// If no separator found, treat the whole string as title
		title = icyString.Trim();
	}

	public void PlayPlaylist(string playlistPath)
	{
		_logger?.LogInformation("PlayPlaylist called with path: {PlaylistPath}", playlistPath);

		if (!_isInitialized)
		{
			_logger?.LogError("Audio engine not initialized");
			StatusChanged?.Invoke("Audio engine not ready");
			return;
		}

		if (_mediaPlayer == null || _libVLC == null)
		{
			_logger?.LogError("Audio components not available");
			StatusChanged?.Invoke("Audio components not available");
			return;
		}

		Stop();

		try
		{
			var fileName = Path.GetFileNameWithoutExtension(playlistPath);
			_logger?.LogInformation("Loading playlist: {FileName}", fileName);
			StatusChanged?.Invoke($"Loading {fileName}...");

			// Parse the playlist to get the first stream URL
			var streamUrl = GetFirstStreamFromPlaylist(playlistPath);
			if (!string.IsNullOrEmpty(streamUrl))
			{
				_logger?.LogInformation("Found stream URL: {StreamUrl}", streamUrl);
				StatusChanged?.Invoke("Connecting to stream...");

				// Create media directly from stream URL
				_currentMedia = new Media(_libVLC, streamUrl, FromType.FromLocation);

				// Set up metadata detection with explicit options
				_currentMedia.AddOption(":meta-policy=1");
				_currentMedia.AddOption(":network-caching=2000");
				_currentMedia.AddOption(":icy-metadata=1");

				_logger?.LogDebug("Setting up metadata event handlers on current media");
				// Set up event handlers before playing
				_currentMedia.MetaChanged += OnMetaChanged;
				_currentMedia.ParsedChanged += OnParsedChanged;

				_logger?.LogDebug("Setting media to player and starting playback");
				_mediaPlayer.Media = _currentMedia;
			}
			else
			{
				_logger?.LogError("Could not extract stream URL from playlist");
				StatusChanged?.Invoke("Invalid playlist file");
				return;
			}

			StatusChanged?.Invoke($"Starting {fileName}...");
			var result = _mediaPlayer.Play();
			_logger?.LogInformation("MediaPlayer.Play() returned: {Result}", result);

			if (!result)
			{
				_logger?.LogError("Failed to start playback");
				StatusChanged?.Invoke("Failed to start playback");
			}
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Exception in PlayPlaylist");
			StatusChanged?.Invoke("Playback error occurred");
		}
	}

	private string? GetFirstStreamFromPlaylist(string playlistPath)
	{
		try
		{
			var lines = File.ReadAllLines(playlistPath);

			// Handle .pls format
			if (Path.GetExtension(playlistPath).Equals(".pls", StringComparison.OrdinalIgnoreCase))
			{
				foreach (var line in lines)
				{
					if (line.StartsWith("File1=", StringComparison.OrdinalIgnoreCase))
					{
						return line.Substring(6).Trim();
					}
				}
			}
			// Handle .m3u format
			else if (Path.GetExtension(playlistPath).Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
			         Path.GetExtension(playlistPath).Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
			{
				foreach (var line in lines)
				{
					if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
					{
						return line.Trim();
					}
				}
			}
		}
		catch (Exception ex)
		{
			StatusChanged?.Invoke($"Error parsing playlist: {ex.Message}");
		}

		return null;
	}

	private void StartMetadataPolling()
	{
		_logger?.LogDebug("Starting metadata polling timer");
		StopMetadataPolling(); // Stop any existing timer

		// Poll for metadata every 2 seconds
		_metadataTimer = new Timer(PollMetadata, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
	}

	private void StopMetadataPolling()
	{
		_logger?.LogDebug("Stopping metadata polling timer");
		_metadataTimer?.Dispose();
		_metadataTimer = null;
	}

	private void PollMetadata(object? state)
	{
		if (_currentMedia != null)
		{
			_logger?.LogDebug("Polling metadata...");
			ExtractAndNotifyTrackInfo(_currentMedia);
		}
	}

	public void Stop()
	{
		_logger?.LogInformation("Stop called");
		StopMetadataPolling();

		if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
		{
			_mediaPlayer.Stop();
		}

		if (_currentMedia != null)
		{
			// Clean up event handlers
			_currentMedia.MetaChanged -= OnMetaChanged;
			_currentMedia.ParsedChanged -= OnParsedChanged;
			_currentMedia.Dispose();
			_currentMedia = null;
		}
	}

	public void Dispose()
	{
		_logger?.LogInformation("Dispose called");
		StopMetadataPolling();
		Stop();
		_mediaPlayer?.Dispose();
		_libVLC?.Dispose();
		_isInitialized = false;
	}
}
