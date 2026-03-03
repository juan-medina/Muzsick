// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muzsick.Audio;

namespace Muzsick.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
	[ObservableProperty] private string _songTitle = "No track loaded";

	[ObservableProperty] private string artistName = "Unknown artist";

	[ObservableProperty] private string _albumName = "Unknown album";

	[ObservableProperty] private bool _isPlaying;

	[ObservableProperty] private string? _playlistPath;

	private Window? _mainWindow;
	private readonly StreamPlayer? _streamPlayer;

	public MainWindowViewModel()
	{
#if DEBUG
		var logger = App.LoggerFactory?.CreateLogger("StreamPlayer");
		_streamPlayer = new StreamPlayer(logger);
#else
		_streamPlayer = new StreamPlayer();
#endif
		_streamPlayer.StatusChanged += OnStatusChanged;
		_streamPlayer.TrackChanged += OnTrackChanged;
		_streamPlayer.Initialize();
	}

	public void SetMainWindow(Window window)
	{
		_mainWindow = window;
	}

	[RelayCommand]
	private void PlayPause()
	{
		if (_streamPlayer == null) return;

		if (IsPlaying)
		{
			// Stop playback
			_streamPlayer.Stop();
			IsPlaying = false;
		}
		else
		{
			// Start playback if we have a playlist
			if (!string.IsNullOrEmpty(PlaylistPath) && File.Exists(PlaylistPath))
			{
				_streamPlayer.PlayPlaylist(PlaylistPath);
				IsPlaying = true;
			}
			else
			{
				// No playlist selected
				SongTitle = "No playlist selected";
				ArtistName = "Please browse for a playlist file";
				AlbumName = "";
			}
		}
	}

	[RelayCommand]
	private async Task BrowsePlaylist()
	{
		if (_mainWindow == null) return;

		// Create file type filters for playlist files
		var fileTypeFilter = new FilePickerFileType("Playlist Files")
		{
			Patterns = new[] { "*.pls", "*.m3u", "*.m3u8" },
			MimeTypes = new[] { "audio/x-scpls", "audio/x-mpegurl", "application/vnd.apple.mpegurl" }
		};

		// Configure the file picker options
		var options = new FilePickerOpenOptions
		{
			Title = "Select Radio Playlist", AllowMultiple = false, FileTypeFilter = new[] { fileTypeFilter }
		};

		// Show the file picker dialog
		var result = await _mainWindow.StorageProvider.OpenFilePickerAsync(options);

		if (result.Count > 0)
		{
			var selectedFile = result[0];
			PlaylistPath = selectedFile.Path.LocalPath;

			// Update the UI to show the selected file
			SongTitle = $"Playlist: {Path.GetFileNameWithoutExtension(PlaylistPath)}";
			ArtistName = "Ready to play";
			AlbumName = PlaylistPath;

			// Stop current playback if playing
			if (IsPlaying)
			{
				_streamPlayer?.Stop();
				IsPlaying = false;
			}
		}
	}

	private void OnTrackChanged(TrackInfo trackInfo)
	{
		// Update UI with new track information
		if (!string.IsNullOrEmpty(trackInfo.Title))
		{
			SongTitle = trackInfo.Title;
		}

		if (!string.IsNullOrEmpty(trackInfo.Artist))
		{
			ArtistName = trackInfo.Artist;
		}
		else
		{
			ArtistName = "Unknown artist";
		}

		if (!string.IsNullOrEmpty(trackInfo.Album))
		{
			AlbumName = trackInfo.Album;
		}
		else
		{
			AlbumName = !string.IsNullOrEmpty(PlaylistPath)
				? Path.GetFileNameWithoutExtension(PlaylistPath)
				: "Unknown album";
		}
	}

	private void OnStatusChanged(string status)
	{
		// Update UI based on stream player status, but don't show debug messages
		if (status.StartsWith("Loading"))
		{
			SongTitle = status;
			ArtistName = "Preparing to connect...";
			AlbumName = "";
		}
		else if (status.StartsWith("Connecting"))
		{
			SongTitle = "Connecting to Stream";
			ArtistName = status;
			AlbumName = "";
		}
		else if (status.StartsWith("Starting"))
		{
			SongTitle = "Starting Playback";
			ArtistName = status;
			AlbumName = "";
		}
		else if (status == "Connected to stream")
		{
			// Show station name initially while waiting for track info
			if (!string.IsNullOrEmpty(PlaylistPath))
			{
				var stationName = Path.GetFileNameWithoutExtension(PlaylistPath);
				SongTitle = stationName;
				ArtistName = "Live Radio Stream";
				AlbumName = "Waiting for track information...";
			}
		}
		else if (status == "Stopped" || status == "Stream ended")
		{
			IsPlaying = false;
			if (!string.IsNullOrEmpty(PlaylistPath))
			{
				SongTitle = $"Playlist: {Path.GetFileNameWithoutExtension(PlaylistPath)}";
				ArtistName = "Ready to play";
				AlbumName = PlaylistPath;
			}
			else
			{
				SongTitle = "No track loaded";
				ArtistName = "Unknown artist";
				AlbumName = "Unknown album";
			}
		}
		else if (status.StartsWith("Audio engine") || status.StartsWith("Invalid") || status.StartsWith("Failed") ||
		         status.StartsWith("Playback error"))
		{
			SongTitle = "Playback Error";
			ArtistName = status;
			AlbumName = "";
			IsPlaying = false;
		}
		// Ignore other status messages to avoid debug noise in UI
	}

	public void Dispose()
	{
		_streamPlayer?.Dispose();
	}
}
