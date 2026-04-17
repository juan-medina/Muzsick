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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muzsick.Audio;
using Muzsick.Commentary;
using Muzsick.Config;
using Muzsick.Discord;
using Muzsick.Metadata;
using Muzsick.MusicSources;
using Muzsick.Tts;
using Muzsick.Update;
using Muzsick.Views;

namespace Muzsick.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
	[ObservableProperty] private string _songTitle = "No track loaded";
	[ObservableProperty] private string _artistName = "Unknown artist";
	[ObservableProperty] private string _albumName = "Unknown album";
	[ObservableProperty] private int _volume = 50;
	[ObservableProperty] private bool _volumeTooltipVisible;
	[ObservableProperty] private string? _updateMessage;
	[ObservableProperty] private string? _artistLastFmUrl;
	[ObservableProperty] private string? _albumLastFmUrl;
	[ObservableProperty] private string? _trackLastFmUrl;

	// Bitmap? — null means no image available, UI falls back to placeholder
	[ObservableProperty] private Bitmap? _albumArt;
	[ObservableProperty] private Bitmap? _artistImage;

	[ObservableProperty] private string _statusMessage = "Vibing with the music";
	[ObservableProperty] private double _statusDotOpacity = 1.0;
	[ObservableProperty] private string _statusDotColor = "#3D8C5A";
	[ObservableProperty] private string _statusTextColor = "#4A7A5E";

	public IReadOnlyDictionary<string, VoiceInfo> TtsAvailableVoices => _ttsBackend.AvailableVoices;
	internal ITtsBackend TtsBackend => _ttsBackend;
	internal AudioMixer AudioMixer => _audioMixer;

	private CancellationTokenSource? _volumeTooltipCts;
	private Window? _mainWindow;
	private readonly AudioMixer _audioMixer;
	private readonly IMetaService _metadataService;
	private readonly ITtsBackend _ttsBackend;
	private readonly HttpClient _httpClient;
	private readonly UpdateService _updateService;
	private CancellationTokenSource _trackCts = new();
	private CancellationTokenSource _voiceoverCts = new();
	private ICommentaryGenerator _commentaryGenerator;
	private bool _isConfigOpen;
	private readonly DiscordPresenceService _discordPresence;
	private readonly HistoryWindowViewModel _historyVm;
	private HistoryWindow? _historyWindow;
	private readonly IMusicSource? _musicSource;
	private Timer? _pulseTimer;
	private double _pulsePhase;

	private const int _maxHistoryEntries = 20;

	public MainWindowViewModel()
	{
#if DEBUG
		var mixerLogger = App.LoggerFactory?.CreateLogger<AudioMixer>();
		var metaLogger = App.LoggerFactory?.CreateLogger<LastFmMetaService>();
#else
		ILogger? mixerLogger = null;
		ILogger? metaLogger = null;
#endif
		_audioMixer = new AudioMixer(mixerLogger);
		_metadataService = new LastFmMetaService(metaLogger);
		_ttsBackend = new KokoroTtsBackend(
			logger: App.LoggerFactory?.CreateLogger<KokoroTtsBackend>());
		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Muzsick/0.1 (https://github.com/juan-medina/muzsick)");
		_updateService = new UpdateService();
		_commentaryGenerator = App.Settings.CommentaryMode == CommentaryMode.Ai
			? App.Settings.AiProvider == AiProvider.Claude
				? new ClaudeCommentaryGenerator(App.LoggerFactory?.CreateLogger<ClaudeCommentaryGenerator>())
				: new OllamaCommentaryGenerator(App.LoggerFactory?.CreateLogger<OllamaCommentaryGenerator>())
			: new TemplateCommentaryGenerator();

		_discordPresence = new DiscordPresenceService(
			App.LoggerFactory?.CreateLogger<DiscordPresenceService>());

		_historyVm = new HistoryWindowViewModel(_audioMixer);

		var settings = SettingsManager.Load();
		if (settings != null)
		{
			App.Settings = settings;
			_volume = Math.Clamp(settings.Volume, 0, 100);
			_audioMixer.SetDjVolume(settings.DjVolume);
		}

		switch (App.Settings.MusicSource)
		{
			case Config.MusicSource.SpotifyApi:
				_musicSource = new SpotifyApiMusicSource(
					App.LoggerFactory?.CreateLogger<SpotifyApiMusicSource>());
				break;

			default:
				// SMTC is Windows-only; on other platforms no source starts
				if (OperatingSystem.IsWindows())
					_musicSource = new SmtcMusicSource(
						App.LoggerFactory?.CreateLogger<SmtcMusicSource>());
				break;
		}

		if (_musicSource is not null)
		{
			_musicSource.TrackChanged += track => _ = HandleTrackAsync(track);
			_musicSource.Start();
		}
	}

	public void SetMainWindow(Window window)
	{
		_mainWindow = window;
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
		_audioMixer.SetDjVolume(value);

		App.Settings.Volume = value;
		SettingsManager.Save(App.Settings);

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
	private async Task OpenConfig()
	{
		if (_mainWindow == null) return;

		_isConfigOpen = true;
		_voiceoverCts.Cancel();

		var voices = (_ttsBackend as KokoroTtsBackend)?.AvailableVoices ??
		             new Dictionary<string, VoiceInfo>();
		var configWindow = new ConfigWindow(isFirstRun: false, voices, _ttsBackend, _audioMixer);
		var saved = await configWindow.ShowDialog<bool>(_mainWindow);

		_isConfigOpen = false;

		if (saved)
		{
			Volume = App.Settings.Volume;
			_commentaryGenerator = App.Settings.CommentaryMode == CommentaryMode.Ai
				? App.Settings.AiProvider == AiProvider.Claude
					? new ClaudeCommentaryGenerator(App.LoggerFactory?.CreateLogger<ClaudeCommentaryGenerator>())
					: new OllamaCommentaryGenerator(App.LoggerFactory?.CreateLogger<OllamaCommentaryGenerator>())
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

	[RelayCommand]
	private void OpenHistory()
	{
		if (_mainWindow == null) return;

		if (_historyWindow != null)
		{
			_historyWindow.Activate();
			return;
		}

		_historyWindow = new HistoryWindow(_historyVm);
		_historyWindow.Closed += (_, _) => _historyWindow = null;
		_historyWindow.Show(_mainWindow);
	}

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

	public async Task HandleTrackAsync(TrackInfo track)
	{
		try
		{
			var cts = new CancellationTokenSource();
			var previous = Interlocked.Exchange(ref _trackCts, cts);
			await previous.CancelAsync();
			previous.Dispose();

			var token = cts.Token;

			SongTitle = !string.IsNullOrEmpty(track.Title) ? track.Title : SongTitle;
			ArtistName = !string.IsNullOrEmpty(track.Artist) ? track.Artist : "Unknown artist";
			AlbumName = !string.IsNullOrEmpty(track.Album) ? track.Album : "Unknown album";

			AlbumArt = null;
			ArtistImage = null;
			ArtistLastFmUrl = null;
			AlbumLastFmUrl = null;
			TrackLastFmUrl = null;
			SetStatus("Checking the liner notes…");

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

			_discordPresence.UpdateTrack(enriched);

			if (_isConfigOpen) return;
			await Task.Delay(TimeSpan.FromSeconds(3), token);

			if (token.IsCancellationRequested) return;

			SetStatus("Finding the right words…");
			string? commentary;
			try
			{
				commentary = await _commentaryGenerator.GenerateAsync(enriched, token);
			}
			catch (OperationCanceledException)
			{
				SetStatus("Vibing with the music");
				return;
			}
			catch (Exception ex)
			{
				App.LoggerFactory?.CreateLogger("MainWindowViewModel")
					.LogWarning("Commentary generation failed: {Message}", ex.Message);

				if (_commentaryGenerator is not TemplateCommentaryGenerator)
				{
					UpdateMessage = "AI commentary unavailable — using template";
					commentary = await new TemplateCommentaryGenerator().GenerateAsync(enriched, token);
				}
				else
				{
					return;
				}
			}

			if (string.IsNullOrWhiteSpace(commentary) || token.IsCancellationRequested) return;

			SetStatus("Warming up the mic…");
			byte[]? wav;
			try
			{
				var voice = App.Settings.TtsVoice;
				wav = await _ttsBackend.SynthesizeAsync(commentary, voice, token);
			}
			catch (OperationCanceledException)
			{
				SetStatus("Vibing with the music");
				return;
			}
			catch (Exception ex)
			{
				App.LoggerFactory?.CreateLogger("MainWindowViewModel")
					.LogWarning("TTS synthesis failed: {Message}", ex.Message);
				return;
			}

			if (token.IsCancellationRequested) return;

			SetStatus("On air…");

			var entry = new HistoryEntry
			{
				Title = enriched.Title,
				Artist = enriched.Artist,
				Album = !string.IsNullOrEmpty(enriched.Album) ? enriched.Album : null,
				Year = enriched.Year,
				TrackLastFmUrl = TrackLastFmUrl,
				ArtistLastFmUrl = ArtistLastFmUrl,
				AlbumLastFmUrl = AlbumLastFmUrl,
				AlbumArt = AlbumArt,
				AnnouncementWav = wav,
			};

			Avalonia.Threading.Dispatcher.UIThread.Post(() =>
			{
				_historyVm.Entries.Insert(0, entry);
				if (_historyVm.Entries.Count > _maxHistoryEntries)
					_historyVm.Entries.RemoveAt(_historyVm.Entries.Count - 1);
			});

			var voiceoverCts = new CancellationTokenSource();
			var previousVoiceover = Interlocked.Exchange(ref _voiceoverCts, voiceoverCts);
			await previousVoiceover.CancelAsync();
			previousVoiceover.Dispose();

			await _audioMixer.PlayVoiceoverAsync(wav, voiceoverCts.Token);
			SetStatus("Vibing with the music");
		}
		catch (Exception ex)
		{
			App.LoggerFactory?.CreateLogger("MainWindowViewModel")
				.LogError(ex, "Unhandled error in HandleTrackAsync");
		}
	}

	private static string? BuildLastFmArtistUrl(string? artist) =>
		string.IsNullOrWhiteSpace(artist)
			? null
			: $"https://www.last.fm/music/{Uri.EscapeDataString(artist).Replace("%20", "+")}";

	private static string? BuildLastFmAlbumUrl(string? artist, string? album) =>
		string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album)
			? null
			: $"https://www.last.fm/music/{Uri.EscapeDataString(artist).Replace("%20", "+")}/{Uri.EscapeDataString(album).Replace("%20", "+")}";

	private static string? BuildLastFmTrackUrl(string? artist, string? title) =>
		string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)
			? null
			: $"https://www.last.fm/music/{Uri.EscapeDataString(artist).Replace("%20", "+")}/{Uri.EscapeDataString(title).Replace("%20", "+")}";

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

	private void SetStatus(string message)
	{
		var idle = message == "Vibing with the music";
		Avalonia.Threading.Dispatcher.UIThread.Post(() =>
		{
			StatusMessage = message;
			StatusDotColor = idle ? "#3D8C5A" : "#FF6B35";
			StatusTextColor = idle ? "#4A7A5E" : "#8A4020";

			_pulseTimer?.Dispose();
			_pulseTimer = null;
			_pulsePhase = 0;

			if (!idle)
			{
				_pulseTimer = new Timer(_ =>
				{
					_pulsePhase = (_pulsePhase + 0.15) % (Math.PI * 2);
					var opacity = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(_pulsePhase));
					Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusDotOpacity = opacity);
				}, null, 0, 40);
			}
			else
			{
				StatusDotOpacity = 1.0;
			}
		});
	}

	public void Dispose()
	{
		_volumeTooltipCts?.Cancel();
		_volumeTooltipCts?.Dispose();
		_trackCts.Cancel();
		_trackCts.Dispose();
		_voiceoverCts.Cancel();
		_voiceoverCts.Dispose();
		_pulseTimer?.Dispose();
		_httpClient.Dispose();
		_audioMixer.Dispose();
		_discordPresence.Dispose();
		_musicSource?.Dispose();
	}
}

