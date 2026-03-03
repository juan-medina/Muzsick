// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muzsick.Audio;
using Muzsick.Metadata;

namespace Muzsick.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
	[ObservableProperty] private string _songTitle = "No track loaded";
	[ObservableProperty] private string _artistName = "Unknown artist";
	[ObservableProperty] private string _albumName = "Unknown album";
	[ObservableProperty] private bool _isPlaying;
	[ObservableProperty] private string? _playlistPath;

	// Null means no image available — UI falls back to placeholder
	[ObservableProperty] private string? _albumArtUrl;
	[ObservableProperty] private string? _artistImageUrl;

	private Window? _mainWindow;
	private readonly StreamPlayer? _streamPlayer;
	private readonly IMusicBrainzService _metadataService;

	public MainWindowViewModel()
	{
#if DEBUG
		var streamLogger = App.LoggerFactory?.CreateLogger("StreamPlayer");
		var metaLogger = App.LoggerFactory?.CreateLogger("MusicBrainzService");
		_streamPlayer = new StreamPlayer(streamLogger);
		_metadataService = new MusicBrainzService(metaLogger);
#else
		_streamPlayer = new StreamPlayer();
		_metadataService = new MusicBrainzService();
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
	private async Task PlayPause()
	{
		if (_streamPlayer == null) return;

		if (IsPlaying)
		{
			_streamPlayer.Stop();
			IsPlaying = false;
		}
		else
		{
			if (!string.IsNullOrEmpty(PlaylistPath) && File.Exists(PlaylistPath))
			{
				await _streamPlayer.PlayPlaylist(PlaylistPath);
				IsPlaying = true;
			}
			else
			{
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

		var fileTypeFilter = new FilePickerFileType("Playlist Files")
		{
			Patterns = ["*.pls", "*.m3u", "*.m3u8"],
			MimeTypes = ["audio/x-scpls", "audio/x-mpegurl", "application/vnd.apple.mpegurl"]
		};

		var options = new FilePickerOpenOptions
		{
			Title = "Select Radio Playlist", AllowMultiple = false, FileTypeFilter = [fileTypeFilter]
		};

		var result = await _mainWindow.StorageProvider.OpenFilePickerAsync(options);

		if (result.Count > 0)
		{
			var selectedFile = result[0];
			PlaylistPath = selectedFile.Path.LocalPath;

			SongTitle = $"Playlist: {Path.GetFileNameWithoutExtension(PlaylistPath)}";
			ArtistName = "Ready to play";
			AlbumName = PlaylistPath;
			AlbumArtUrl = null;
			ArtistImageUrl = null;

			if (IsPlaying)
			{
				_streamPlayer?.Stop();
				IsPlaying = false;
			}
		}
	}

	private async void OnTrackChanged(TrackInfo track)
	{
		try
		{
			// Update text immediately from ICY data
			SongTitle = !string.IsNullOrEmpty(track.Title) ? track.Title : SongTitle;
			ArtistName = !string.IsNullOrEmpty(track.Artist) ? track.Artist : "Unknown artist";
			AlbumName = !string.IsNullOrEmpty(track.Album)
				? track.Album
				: !string.IsNullOrEmpty(PlaylistPath)
					? Path.GetFileNameWithoutExtension(PlaylistPath)
					: "Unknown album";

			// Clear images while fetching — UI shows placeholders
			AlbumArtUrl = null;
			ArtistImageUrl = null;

			// Enrich in the background — stub returns null URLs so placeholders stay visible for now
			var (enriched, artist) = await _metadataService.EnrichAsync(track);

			AlbumArtUrl = enriched.CoverArtUrl;
			ArtistImageUrl = artist.ImageUrl;

			// Use enriched album name if ICY didn't provide one
			if (string.IsNullOrEmpty(track.Album) && !string.IsNullOrEmpty(enriched.Album))
				AlbumName = enriched.Album;
		}
		catch (Exception ex)
		{
			App.LoggerFactory?.CreateLogger("MainWindowViewModel")
				.LogError(ex, "Error enriching track metadata");
		}
	}

	private void OnStatusChanged(string status)
	{
		if (status.StartsWith("Loading"))
		{
			SongTitle = status;
			ArtistName = "Preparing to connect...";
			AlbumName = "";
			AlbumArtUrl = null;
			ArtistImageUrl = null;
		}
		else if (status.StartsWith("Starting"))
		{
			SongTitle = "Starting Playback";
			ArtistName = status;
			AlbumName = "";
		}
		else if (status == "Connected to stream")
		{
			if (!string.IsNullOrEmpty(PlaylistPath))
			{
				SongTitle = Path.GetFileNameWithoutExtension(PlaylistPath);
				ArtistName = "Live Radio Stream";
				AlbumName = "Waiting for track information...";
			}
		}
		else if (status == "Stopped" || status == "Stream ended")
		{
			IsPlaying = false;
			AlbumArtUrl = null;
			ArtistImageUrl = null;
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
		else if (status.StartsWith("Failed") || status.StartsWith("Playback error") || status == "Connection error")
		{
			SongTitle = "Playback Error";
			ArtistName = status;
			AlbumName = "";
			IsPlaying = false;
		}
	}

	public void Dispose()
	{
		_streamPlayer?.Dispose();
	}
}
