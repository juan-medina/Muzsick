// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Muzsick.Commentary;
using Muzsick.Config;
using Muzsick.Metadata;
using Muzsick.Tts;
using Muzsick.Update;
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
	[ObservableProperty] private string? _updateMessage;
	[ObservableProperty] private string? _artistLastFmUrl;
	[ObservableProperty] private string? _albumLastFmUrl;
	[ObservableProperty] private string? _trackLastFmUrl;
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
	private readonly UpdateService _updateService;
	private CancellationTokenSource _trackCts = new();
	private CancellationTokenSource _voiceoverCts = new();
	private byte[]? _lastAnnouncementWav;
	private ICommentaryGenerator _commentaryGenerator;
	private bool _isConfigOpen;

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
		_updateService = new UpdateService();
		_commentaryGenerator = App.Settings.CommentaryMode == CommentaryMode.Ai
			? new OllamaCommentaryGenerator(App.LoggerFactory?.CreateLogger<OllamaCommentaryGenerator>())
			: new TemplateCommentaryGenerator();

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

		// Fire-and-forget: check for updates in the background after the view is ready.
		_ = CheckForUpdatesAsync();
	}

	private async Task CheckForUpdatesAsync()
	{
		var logger = App.LoggerFactory?.CreateLogger<UpdateService>();
		var staged = await _updateService.CheckAndApplyUpdatesAsync(logger);
		if (staged)
			UpdateMessage = "A new version has been downloaded and will be applied on next restart.";
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
		ArtistLastFmUrl = null;
		AlbumLastFmUrl = null;
		TrackLastFmUrl = null;
	}

	[RelayCommand]
	private async Task OpenConfig()
	{
		if (_mainWindow == null) return;

		_isConfigOpen = true;
		_voiceoverCts.Cancel();

		var voices = (_ttsBackend as KokoroTtsBackend)?.AvailableVoices ?? new Dictionary<string, VoiceInfo>();
		var configWindow = new ConfigWindow(isFirstRun: false, voices, _ttsBackend, _audioMixer);
		var saved = await configWindow.ShowDialog<bool>(_mainWindow);

		_isConfigOpen = false;

		if (saved)
		{
			_commentaryGenerator = App.Settings.CommentaryMode == CommentaryMode.Ai
				? new OllamaCommentaryGenerator(App.LoggerFactory?.CreateLogger<OllamaCommentaryGenerator>())
				: new TemplateCommentaryGenerator();
			UpdateMessage = null;
		}
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
			ArtistLastFmUrl = null;
			AlbumLastFmUrl = null;
			TrackLastFmUrl = null;

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

			ArtistLastFmUrl = BuildLastFmArtistUrl(enriched.Artist);
			AlbumLastFmUrl = BuildLastFmAlbumUrl(enriched.Artist, enriched.Album);
			TrackLastFmUrl = BuildLastFmTrackUrl(enriched.Artist, enriched.Title);

			// §5.6 — wait before generating commentary to avoid interrupting the song intro.
			// Also skip commentary entirely while the settings window is open.
			if (_isConfigOpen) return;
			await Task.Delay(TimeSpan.FromSeconds(3), token);

			if (token.IsCancellationRequested) return;

			string? announcement;
			try
			{
				announcement = await _commentaryGenerator.GenerateAsync(enriched, token);
			}
			catch (TimeoutException)
			{
				announcement = null;
			}
			catch (HttpRequestException ex)
			{
				App.LoggerFactory?.CreateLogger("MainWindowViewModel")
					.LogWarning("Ollama unreachable during track change: {Message}", ex.Message);
				announcement = null;
			}

			// AI mode: if Ollama failed (returned null), fall back to template and warn the user.
			if (announcement == null)
			{
				announcement = AnnouncementTemplateRenderer.Render(App.Settings.AnnouncementTemplate, enriched);
				Avalonia.Threading.Dispatcher.UIThread.Post(() =>
					UpdateMessage = "⚠ AI commentary unavailable — using template");
			}
			else if (App.Settings.CommentaryMode == CommentaryMode.Ai)
			{
				// Ollama responded — clear any previous warning.
				Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateMessage = null);
			}

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

	[RelayCommand]
	private void OpenLastFm(string? url)
	{
		if (string.IsNullOrEmpty(url)) return;
		try
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			App.LoggerFactory?.CreateLogger("MainWindowViewModel")
				.LogWarning("Failed to open Last.fm URL: {Message}", ex.Message);
		}
	}

	private static string? BuildLastFmArtistUrl(string? artist) =>
		string.IsNullOrWhiteSpace(artist) ? null
			: $"https://www.last.fm/music/{Uri.EscapeDataString(artist).Replace("%20", "+")}";

	private static string? BuildLastFmAlbumUrl(string? artist, string? album) =>
		string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album) ? null
			: $"https://www.last.fm/music/{Uri.EscapeDataString(artist).Replace("%20", "+")}/{Uri.EscapeDataString(album).Replace("%20", "+")}";

	private static string? BuildLastFmTrackUrl(string? artist, string? title) =>
		string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) ? null
			: $"https://www.last.fm/music/{Uri.EscapeDataString(artist).Replace("%20", "+")}/_/{Uri.EscapeDataString(title).Replace("%20", "+")}";

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
				ArtistLastFmUrl = null;
				AlbumLastFmUrl = null;
				TrackLastFmUrl = null;
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
