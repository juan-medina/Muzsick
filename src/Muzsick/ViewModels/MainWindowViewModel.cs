// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
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

	// Bitmap? — null means no image available, UI falls back to placeholder
	[ObservableProperty] private Bitmap? _albumArt;
	[ObservableProperty] private Bitmap? _artistImage;

	private Window? _mainWindow;
	private readonly StreamPlayer? _streamPlayer;
	private readonly IMetaService _metadataService;
	private readonly HttpClient _httpClient;
	private Guid _currentTrackToken;

	public MainWindowViewModel()
	{
#if DEBUG
		var streamLogger = App.LoggerFactory?.CreateLogger("StreamPlayer");
		var metaLogger = App.LoggerFactory?.CreateLogger("MusicBrainzService");
		_streamPlayer = new StreamPlayer(streamLogger);
		_metadataService = new MusicBrainzMetaService(metaLogger);

#else
		_streamPlayer = new StreamPlayer();
		_metadataService = new MusicBrainzService();
#endif
		_streamPlayer.StatusChanged += OnStatusChanged;
		_streamPlayer.TrackChanged += OnTrackChanged;
		_streamPlayer.Initialize();
		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Muzsick/0.1 (https://github.com/juan-medina/muzsick)");
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
			AlbumArt = null;
			ArtistImage = null;

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
			var token = Guid.NewGuid();
			_currentTrackToken = token;

			SongTitle = !string.IsNullOrEmpty(track.Title) ? track.Title : SongTitle;
			ArtistName = !string.IsNullOrEmpty(track.Artist) ? track.Artist : "Unknown artist";
			AlbumName = !string.IsNullOrEmpty(track.Album)
				? track.Album
				: !string.IsNullOrEmpty(PlaylistPath)
					? Path.GetFileNameWithoutExtension(PlaylistPath)
					: "Unknown album";

			AlbumArt = null;
			ArtistImage = null;

			var (enriched, artist) = await _metadataService.EnrichAsync(track);

			if (token != _currentTrackToken) return;

			if (!string.IsNullOrEmpty(enriched.CoverArtUrl))
				AlbumArt = await LoadBitmapAsync(enriched.CoverArtUrl);

			if (!string.IsNullOrEmpty(artist.ImageUrl))
				ArtistImage = await LoadBitmapAsync(artist.ImageUrl);

			if (string.IsNullOrEmpty(track.Album) && !string.IsNullOrEmpty(enriched.Album))
				AlbumName = enriched.Album;
		}
		catch (Exception ex)
		{
			App.LoggerFactory?.CreateLogger("MainWindowViewModel")
				.LogError(ex, "Error enriching track metadata");
		}
	}

	/// <summary>
	/// Downloads an image from a URL and decodes it into an Avalonia Bitmap.
	/// Avalonia cannot load HTTP URLs from a string binding on Image.Source —
	/// a Bitmap object is required.
	/// </summary>
	private async Task<Bitmap?> LoadBitmapAsync(string url)
	{
		try
		{
			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode)
			{
				App.LoggerFactory?.CreateLogger("MainWindowViewModel")
					.LogWarning("Image download failed: {StatusCode} for {Url}", response.StatusCode, url);
				return null;
			}

			var bytes = await response.Content.ReadAsByteArrayAsync();
			using var ms = new MemoryStream(bytes);
			return new Bitmap(ms);
		}
		catch (Exception ex)
		{
			App.LoggerFactory?.CreateLogger("MainWindowViewModel")
				.LogWarning("Image load failed: {Message} for {Url}", ex.Message, url);
			return null;
		}
	}

	private void OnStatusChanged(string status)
	{
		if (status.StartsWith("Loading"))
		{
			SongTitle = status;
			ArtistName = "Preparing to connect...";
			AlbumName = "";
			AlbumArt = null;
			ArtistImage = null;
		}
		else if (status.StartsWith("Starting"))
		{
			SongTitle = "Starting Playback";
			ArtistName = status;
			AlbumName = "";
		}
		else switch (status)
		{
			case "Connected to stream" when string.IsNullOrEmpty(PlaylistPath):
				return;
			case "Connected to stream":
				SongTitle = Path.GetFileNameWithoutExtension(PlaylistPath);
				ArtistName = "Live Radio Stream";
				AlbumName = "Waiting for track information...";
				break;
			case "Stopped":
			case "Stream ended":
			{
				IsPlaying = false;
				AlbumArt = null;
				ArtistImage = null;
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

				break;
			}
			default:
			{
				if (status.StartsWith("Failed") || status.StartsWith("Playback error") || status == "Connection error")
				{
					SongTitle = "Playback Error";
					ArtistName = status;
					AlbumName = "";
					IsPlaying = false;
				}

				break;
			}
		}
	}

	public void Dispose()
	{
		_httpClient.Dispose();
		_streamPlayer?.Dispose();
	}
}
