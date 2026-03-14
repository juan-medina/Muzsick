// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muzsick.Audio;
using Muzsick.Config;
using Muzsick.Metadata;
using Muzsick.Tts;
using Muzsick.Views;

namespace Muzsick.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
	[ObservableProperty] private string _songTitle = "No track loaded";
	[ObservableProperty] private string _artistName = "Unknown artist";
	[ObservableProperty] private string _albumName = "Unknown album";
	[ObservableProperty] private bool _isPlaying;
	[ObservableProperty] private string? _playlistPath;
	[ObservableProperty] private int _volume = 50;
	[ObservableProperty] private bool _volumeTooltipVisible;
	public IReadOnlyDictionary<string, VoiceInfo> TtsAvailableVoices => _ttsBackend.AvailableVoices;
	internal ITtsBackend TtsBackend => _ttsBackend;
	internal AudioMixer AudioMixer => _audioMixer;

	private CancellationTokenSource? _volumeTooltipCts;

	// Bitmap? — null means no image available, UI falls back to placeholder
	[ObservableProperty] private Bitmap? _albumArt;
	[ObservableProperty] private Bitmap? _artistImage;

	private Window? _mainWindow;
	private readonly AudioMixer _audioMixer;
	private readonly StreamPlayer? _streamPlayer;
	private readonly IMetaService _metadataService;
	private readonly ITtsBackend _ttsBackend;
	private readonly HttpClient _httpClient;
	private CancellationTokenSource _trackCts = new();
	private CancellationTokenSource _voiceoverCts = new();
	private byte[]? _lastAnnouncementWav;

	public MainWindowViewModel()
	{
#if DEBUG
		var mixerLogger = App.LoggerFactory?.CreateLogger<AudioMixer>();
		var streamLogger = App.LoggerFactory?.CreateLogger<StreamPlayer>();
		var metaLogger = App.LoggerFactory?.CreateLogger<LastFmMetaService>();
#else
		ILogger? mixerLogger = null;
		ILogger? streamLogger = null;
		ILogger? metaLogger = null;
#endif
		_audioMixer = new AudioMixer(mixerLogger);
		_streamPlayer = new StreamPlayer(_audioMixer, streamLogger);
		_metadataService = new LastFmMetaService(metaLogger);
		_ttsBackend = new KokoroTtsBackend(
			logger: App.LoggerFactory?.CreateLogger<KokoroTtsBackend>());
		_streamPlayer.StatusChanged += OnStatusChanged;
		_streamPlayer.TrackChanged += OnTrackChanged;
		_streamPlayer.Initialize();
		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Muzsick/0.1 (https://github.com/juan-medina/muzsick)");

		var settings = SettingsManager.Load();
		if (settings == null) return;
		_volume = Math.Clamp(settings.Volume, 0, 100);
		_audioMixer.SetVolume(_volume);

		if (!string.IsNullOrEmpty(settings.LastPlaylistPath) && File.Exists(settings.LastPlaylistPath))
			ApplyPlaylistPath(settings.LastPlaylistPath);
	}

	public void SetMainWindow(Window window)
	{
		_mainWindow = window;
	}

	partial void OnVolumeChanged(int value)
	{
		_audioMixer.SetVolume(value);

		var settings = SettingsManager.Load() ?? new AppSettings();
		settings.Volume = value;
		SettingsManager.Save(settings);

		// Show floating label, cancel any previous hide timer
		_volumeTooltipCts?.Cancel();
		_volumeTooltipCts = new CancellationTokenSource();
		VolumeTooltipVisible = true;

		var token = _volumeTooltipCts.Token;
		_ = Task.Run(async () =>
		{
			await Task.Delay(1500, token);
			if (!token.IsCancellationRequested)
				Avalonia.Threading.Dispatcher.UIThread.Post(() => VolumeTooltipVisible = false);
		}, token).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnCanceled);
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
			ApplyPlaylistPath(result[0].Path.LocalPath);

			var settings = SettingsManager.Load() ?? new AppSettings();
			settings.LastPlaylistPath = PlaylistPath;
			SettingsManager.Save(settings);
		}
	}

	private void ApplyPlaylistPath(string path)
	{
		if (IsPlaying)
		{
			_streamPlayer?.Stop();
			IsPlaying = false;
		}

		PlaylistPath = path;
		SongTitle = $"Playlist: {Path.GetFileNameWithoutExtension(PlaylistPath)}";
		ArtistName = "Ready to play";
		AlbumName = PlaylistPath;
		AlbumArt = null;
		ArtistImage = null;
	}

	[RelayCommand]
	private async Task OpenConfig()
	{
		if (_mainWindow == null) return;

		var voices = (_ttsBackend as KokoroTtsBackend)?.AvailableVoices ?? new Dictionary<string, VoiceInfo>();
		var configWindow = new ConfigWindow(isFirstRun: false, voices, _ttsBackend, _audioMixer);
		await configWindow.ShowDialog<bool>(_mainWindow);
	}

	[RelayCommand]
	private async Task OpenAbout()
	{
		if (_mainWindow == null) return;
		await new AboutWindow().ShowDialog(_mainWindow);
	}

	private async void OnTrackChanged(TrackInfo track)
	{
		try
		{
			// Cancel everything still running for the previous track.
			var cts = new CancellationTokenSource();
			var previous = Interlocked.Exchange(ref _trackCts, cts);
			await previous.CancelAsync();
			previous.Dispose();


			var token = cts.Token;

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

			if (token.IsCancellationRequested) return;

			if (!string.IsNullOrEmpty(enriched.CoverArtUrl))
				AlbumArt = await LoadBitmapAsync(enriched.CoverArtUrl);

			if (token.IsCancellationRequested) return;

			if (!string.IsNullOrEmpty(artist.ImageUrl))
				ArtistImage = await LoadBitmapAsync(artist.ImageUrl);

			if (token.IsCancellationRequested) return;

			ArtistName = !string.IsNullOrEmpty(enriched.Artist) ? enriched.Artist : "Unknown artist";

			var albumName = !string.IsNullOrEmpty(enriched.Album)
				? enriched.Album
				: !string.IsNullOrEmpty(track.Album)
					? track.Album
					: null;

			if (albumName != null)
				AlbumName = !string.IsNullOrEmpty(enriched.Year)
					? $"{albumName} ({enriched.Year})"
					: albumName;

			// §5.6 — wait before generating commentary to avoid interrupting the song intro.
			await Task.Delay(TimeSpan.FromSeconds(3), token);

			if (token.IsCancellationRequested) return;

			var announcement = AnnouncementTemplateRenderer.Render(App.Settings.AnnouncementTemplate, enriched);
			var wavBytes = await _ttsBackend.SynthesizeAsync(announcement, App.Settings.TtsVoice, token);

			if (token.IsCancellationRequested) return;

			if (wavBytes is { Length: > 0 })
			{
				_lastAnnouncementWav = wavBytes;
				Avalonia.Threading.Dispatcher.UIThread.Post(
					ReplayAnnouncementCommand.NotifyCanExecuteChanged);
				await PlayAnnouncementAsync(wavBytes);
			}
		}
		catch (OperationCanceledException)
		{
			// New track arrived — silently discard.
		}
		catch (Exception ex)
		{
			App.LoggerFactory?.CreateLogger("MainWindowViewModel")
				.LogError(ex, "Error processing track");
		}
	}

	// Atomically cancels any playing announcement and starts a new one.
	// This is the single choke-point — call it from everywhere (track change, replay, config preview).
	private async Task PlayAnnouncementAsync(byte[] wavBytes)
	{
		var cts = new CancellationTokenSource();
		var prev = Interlocked.Exchange(ref _voiceoverCts, cts);
		prev.Cancel();
		prev.Dispose();
		await _audioMixer.PlayVoiceoverAsync(wavBytes, cts.Token);
	}

	[RelayCommand(CanExecute = nameof(CanReplayAnnouncement))]
	private async Task ReplayAnnouncement()
	{
		await PlayAnnouncementAsync(_lastAnnouncementWav!);
	}

	private bool CanReplayAnnouncement() => _lastAnnouncementWav is { Length: > 0 };

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
		else
			switch (status)
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
					if (status.StartsWith("Failed") || status.StartsWith("Playback error") ||
					    status == "Connection error")
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
		_volumeTooltipCts?.Cancel();
		_volumeTooltipCts?.Dispose();
		_trackCts.Cancel();
		_trackCts.Dispose();
		_voiceoverCts.Cancel();
		_voiceoverCts.Dispose();
		_httpClient.Dispose();
		_streamPlayer?.Dispose();
		_audioMixer.Dispose();
	}
}
