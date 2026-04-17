// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muzsick.Audio;
using Muzsick.Commentary;
using Muzsick.Config;
using Muzsick.Metadata;
using Muzsick.Spotify;
using Muzsick.Tts;
using SpotifyAPI.Web;

namespace Muzsick.ViewModels;

public enum PreviewState
{
	Idle,
	Generating,
	Synthesising,
	Playing,
	Done,
	Failed,
}

public enum OllamaCheckState
{
	Idle,
	Checking,
	Ok,
	ModelMissing,
	Failed,
}

public enum ClaudeCheckState
{
	Idle,
	Checking,
	Ok,
	Failed,
}

public enum SpotifyCheckState
{
	Idle,
	Checking,
	Ok,
	Failed,
}

public enum VoiceTestState
{
	Idle,
	Synthesising,
	Playing,
	Done,
	Failed,
}

public partial class ConfigWindowViewModel(
	bool isFirstRun,
	IReadOnlyDictionary<string, VoiceInfo> availableVoices,
	ITtsBackend ttsBackend,
	AudioMixer audioMixer)
	: ViewModelBase
{
	// ── Nav ─────────────────────────────────────────────────────────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsVoiceSection))]
	[NotifyPropertyChangedFor(nameof(IsCommentarySection))]
	[NotifyPropertyChangedFor(nameof(IsAiProviderSection))]
	[NotifyPropertyChangedFor(nameof(IsAiPromptSection))]
	[NotifyPropertyChangedFor(nameof(IsMusicSourceSection))]
	private int _selectedNavIndex;

	public bool IsVoiceSection => SelectedNavIndex == 0;
	public bool IsMusicSourceSection => SelectedNavIndex == 1;
	public bool IsCommentarySection => SelectedNavIndex == 2;
	public bool IsAiProviderSection => SelectedNavIndex == 3;
	public bool IsAiPromptSection => SelectedNavIndex == 4;

	[RelayCommand]
	private void GoToAiProvider() => SelectedNavIndex = 3;

	[RelayCommand]
	private void GoToAiPrompt() => SelectedNavIndex = 4;

	[RelayCommand]
	private void GoToCommentary() => SelectedNavIndex = 2;


	// ── Music Source ─────────────────────────────────────────────────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSmtcSource))]
	[NotifyPropertyChangedFor(nameof(IsSpotifyApiSource))]
	private MusicSource _musicSource = App.Settings.MusicSource;

	public bool IsSmtcSource
	{
		get => MusicSource == MusicSource.Smtc;
		set { if (value) MusicSource = MusicSource.Smtc; }
	}

	public bool IsSpotifyApiSource
	{
		get => MusicSource == MusicSource.SpotifyApi;
		set { if (value) MusicSource = MusicSource.SpotifyApi; }
	}

	// SMTC is only available on Windows
	public bool IsSmtcAvailable => OperatingSystem.IsWindows();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsConnected))]
	[NotifyPropertyChangedFor(nameof(IsDisconnected))]
	[NotifyPropertyChangedFor(nameof(IsConnecting))]
	[NotifyPropertyChangedFor(nameof(ConnectButtonLabel))]
	private SpotifyConnectState _spotifyConnectState =
		string.IsNullOrEmpty(App.Settings.SpotifyRefreshToken)
			? SpotifyConnectState.Disconnected
			: SpotifyConnectState.Connected;

	public bool IsConnected => SpotifyConnectState == SpotifyConnectState.Connected;
	public bool IsDisconnected => SpotifyConnectState != SpotifyConnectState.Connected;
	public bool IsConnecting => SpotifyConnectState == SpotifyConnectState.Connecting;

	public string ConnectButtonLabel => SpotifyConnectState switch
	{
		SpotifyConnectState.Connecting => "Connecting...",
		SpotifyConnectState.Connected  => "Reconnect",
		SpotifyConnectState.Failed     => "Retry",
		_                              => "Connect Spotify",
	};

	[ObservableProperty] private string _spotifyClientId = App.Settings.SpotifyClientId;
	[ObservableProperty] private string _spotifyConnectError = "";
	public bool HasSpotifyConnectError => !string.IsNullOrEmpty(SpotifyConnectError);

	[RelayCommand]
	private async Task ConnectSpotify()
	{
		if (string.IsNullOrWhiteSpace(SpotifyClientId))
		{
			SpotifyConnectError = "Enter your Client ID first.";
			OnPropertyChanged(nameof(HasSpotifyConnectError));
			return;
		}

		SpotifyConnectError = "";
		OnPropertyChanged(nameof(HasSpotifyConnectError));
		SpotifyConnectState = SpotifyConnectState.Connecting;

		// Save client ID immediately so it persists even if auth is cancelled
		App.Settings.SpotifyClientId = SpotifyClientId;
		SettingsManager.Save(App.Settings);

		await _spotifyAuthCts.CancelAsync();
		_spotifyAuthCts.Dispose();
		_spotifyAuthCts = new CancellationTokenSource();

		var authService = new SpotifyAuthService(
			App.LoggerFactory?.CreateLogger<SpotifyAuthService>());

		var refreshToken = await authService.AuthorizeAsync(SpotifyClientId, _spotifyAuthCts.Token);

		if (refreshToken is null)
		{
			SpotifyConnectState = SpotifyConnectState.Failed;
			SpotifyConnectError = "Authentication cancelled or failed. Check your Client ID and try again.";
			OnPropertyChanged(nameof(HasSpotifyConnectError));
			return;
		}

		App.Settings.SpotifyRefreshToken = refreshToken;
		SettingsManager.Save(App.Settings);
		SpotifyConnectState = SpotifyConnectState.Connected;
	}

	[RelayCommand]
	private void DisconnectSpotify()
	{
		_spotifyAuthCts.Cancel();
		App.Settings.SpotifyRefreshToken = "";
		SettingsManager.Save(App.Settings);
		SpotifyConnectState = SpotifyConnectState.Disconnected;
		SpotifyConnectError = "";
		OnPropertyChanged(nameof(HasSpotifyConnectError));
	}

	// ── Spotify connection check ──────────────────────────────────────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSpotifyCheckOk))]
	[NotifyPropertyChangedFor(nameof(IsSpotifyCheckFailed))]
	[NotifyPropertyChangedFor(nameof(IsSpotifyChecking))]
	[NotifyCanExecuteChangedFor(nameof(CheckSpotifyCommand))]
	private SpotifyCheckState _spotifyCheckState = SpotifyCheckState.Idle;

	public bool IsSpotifyCheckOk => SpotifyCheckState == SpotifyCheckState.Ok;
	public bool IsSpotifyCheckFailed => SpotifyCheckState == SpotifyCheckState.Failed;
	public bool IsSpotifyChecking => SpotifyCheckState == SpotifyCheckState.Checking;

	private bool CanCheckSpotify() => SpotifyCheckState != SpotifyCheckState.Checking
		&& !string.IsNullOrEmpty(App.Settings.SpotifyRefreshToken);

	[RelayCommand(CanExecute = nameof(CanCheckSpotify))]
	private async Task CheckSpotify()
	{
		SpotifyCheckState = SpotifyCheckState.Checking;
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			var refreshResponse = await new OAuthClient().RequestToken(
				new PKCETokenRefreshRequest(SpotifyClientId, App.Settings.SpotifyRefreshToken), cts.Token);

			// PKCE refresh tokens are single-use — persist the new one immediately
			App.Settings.SpotifyRefreshToken = refreshResponse.RefreshToken;
			SettingsManager.Save(App.Settings);

			using var client = new HttpClient();
			client.DefaultRequestHeaders.Authorization =
				new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", refreshResponse.AccessToken);

			var response = await client.GetAsync(
				"https://api.spotify.com/v1/me", cts.Token);

			SpotifyCheckState = response.IsSuccessStatusCode
				? SpotifyCheckState.Ok
				: SpotifyCheckState.Failed;
		}
		catch
		{
			SpotifyCheckState = SpotifyCheckState.Failed;
		}
	}


	// ── Settings fields ──────────────────────────────────────────────────────

	[ObservableProperty] private VoiceInfo? _selectedVoice;




	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(TemplateError))]
	[NotifyPropertyChangedFor(nameof(HasTemplateError))]
	[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	private string _announcementTemplate = App.Settings.AnnouncementTemplate;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAiMode))]
	[NotifyPropertyChangedFor(nameof(IsTemplateModeSelected))]
	[NotifyPropertyChangedFor(nameof(IsAiModeSelected))]
	[NotifyPropertyChangedFor(nameof(AiPromptError))]
	[NotifyPropertyChangedFor(nameof(HasAiPromptError))]
	[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	private CommentaryMode _commentaryMode = App.Settings.CommentaryMode;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AiPromptError))]
	[NotifyPropertyChangedFor(nameof(HasAiPromptError))]
	[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	private string _aiPrompt = App.Settings.AiPrompt;

	[ObservableProperty] private string _ollamaUrl = App.Settings.OllamaUrl;

	[ObservableProperty] [NotifyPropertyChangedFor(nameof(OllamaModelMissingMessage))]
	private string _ollamaModel = App.Settings.OllamaModel;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOllamaProvider))]
	[NotifyPropertyChangedFor(nameof(IsClaudeProvider))]
	private AiProvider _aiProvider = App.Settings.AiProvider;

	[ObservableProperty] private string _claudeApiKey = App.Settings.ClaudeApiKey;

	[ObservableProperty] private string _claudeModel = App.Settings.ClaudeModel;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasClaudeModelsRefreshError))]
	private string? _claudeModelsRefreshError;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RefreshClaudeModelsCommand))]
	private bool _isRefreshingClaudeModels;

	private static readonly string[] _knownClaudeModels =
	[
		"claude-haiku-4-5",
		"claude-sonnet-4-5",
		"claude-opus-4-5",
		"claude-haiku-3-5",
		"claude-sonnet-3-5",
		"claude-opus-3",
	];

	public ObservableCollection<string> ClaudeModels { get; } = BuildInitialClaudeModels();

	public bool HasClaudeModelsRefreshError => !string.IsNullOrEmpty(ClaudeModelsRefreshError);

	private static ObservableCollection<string> BuildInitialClaudeModels()
	{
		var list = new ObservableCollection<string>(_knownClaudeModels);
		var current = App.Settings.ClaudeModel;
		if (!string.IsNullOrEmpty(current) && !list.Contains(current))
			list.Insert(0, current);
		return list;
	}

	// ── Computed settings properties ─────────────────────────────────────────

	public bool IsAiMode => CommentaryMode == CommentaryMode.Ai;

	public bool IsTemplateModeSelected
	{
		get => CommentaryMode == CommentaryMode.Template;
		set
		{
			if (value) CommentaryMode = CommentaryMode.Template;
		}
	}

	public bool IsAiModeSelected
	{
		get => CommentaryMode == CommentaryMode.Ai;
		set
		{
			if (value) CommentaryMode = CommentaryMode.Ai;
		}
	}

	public bool IsOllamaProvider
	{
		get => AiProvider == AiProvider.Ollama;
		set
		{
			if (value) AiProvider = AiProvider.Ollama;
		}
	}

	public bool IsClaudeProvider
	{
		get => AiProvider == AiProvider.Claude;
		set
		{
			if (value) AiProvider = AiProvider.Claude;
		}
	}

	public string? TemplateError => string.IsNullOrWhiteSpace(AnnouncementTemplate)
		? "The announcement template cannot be empty."
		: null;

	public string? AiPromptError => IsAiMode && string.IsNullOrWhiteSpace(AiPrompt)
		? "An AI prompt is required when AI mode is selected."
		: null;

	public bool HasTemplateError => TemplateError != null;
	public bool HasAiPromptError => AiPromptError != null;

	[ObservableProperty] private string _templateSample = "";

	// ── Prompt library ───────────────────────────────────────────────────────

	public IReadOnlyList<PromptLibraryEntry> PromptLibrary { get; } = Commentary.PromptLibrary.Entries;

	[ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPromptLibraryOpen))]
	private bool _promptLibraryVisible;

	public bool IsPromptLibraryOpen => PromptLibraryVisible;

	[RelayCommand]
	private void TogglePromptLibrary() => PromptLibraryVisible = !PromptLibraryVisible;

	[RelayCommand]
	private async Task SavePrompt()
	{
		if (_window == null) return;
		var file = await _window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
		{
			Title = "Save prompt",
			DefaultExtension = "txt",
			FileTypeChoices =
				[new Avalonia.Platform.Storage.FilePickerFileType("Text files") { Patterns = ["*.txt"] }],
		});
		if (file != null)
			await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, AiPrompt);
	}

	[RelayCommand]
	private async Task LoadPromptFromFile()
	{
		if (_window == null) return;
		var files = await _window.StorageProvider.OpenFilePickerAsync(
			new Avalonia.Platform.Storage.FilePickerOpenOptions
			{
				Title = "Load prompt",
				AllowMultiple = false,
				FileTypeFilter =
					[new Avalonia.Platform.Storage.FilePickerFileType("Text files") { Patterns = ["*.txt"] }],
			});
		var file = files.FirstOrDefault();
		if (file != null)
			AiPrompt = await System.IO.File.ReadAllTextAsync(file.Path.LocalPath);
	}

	[RelayCommand]
	private void LoadPrompt(PromptLibraryEntry entry)
	{
		AiPrompt = entry.Prompt;
		PromptLibraryVisible = false;
	}

	// ── Voice test ───────────────────────────────────────────────────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(VoiceTestButtonLabel))]
	[NotifyPropertyChangedFor(nameof(IsVoiceTesting))]
	[NotifyCanExecuteChangedFor(nameof(TestVoiceCommand))]
	private VoiceTestState _voiceTestState = VoiceTestState.Idle;

	[ObservableProperty] [NotifyPropertyChangedFor(nameof(HasVoiceTestError))]
	private string? _voiceTestError;

	public bool IsVoiceTesting => VoiceTestState is VoiceTestState.Synthesising or VoiceTestState.Playing;
	public bool HasVoiceTestError => !string.IsNullOrEmpty(VoiceTestError);

	public string VoiceTestButtonLabel => VoiceTestState switch
	{
		VoiceTestState.Synthesising => "Synthesising…",
		VoiceTestState.Playing => "Playing…",
		VoiceTestState.Done => "Played",
		VoiceTestState.Failed => "Failed",
		_ => "Hear Voice",
	};

	private CancellationTokenSource _voiceTestCts = new();

	private bool CanTestVoice() => VoiceTestState is not (VoiceTestState.Synthesising or VoiceTestState.Playing);

	[RelayCommand(CanExecute = nameof(CanTestVoice))]
	private async Task TestVoice()
	{
		await _voiceTestCts.CancelAsync();
		_voiceTestCts.Dispose();
		_voiceTestCts = new CancellationTokenSource();
		var token = _voiceTestCts.Token;

		VoiceTestError = null;
		VoiceTestState = VoiceTestState.Synthesising;

		try
		{
			var voice = SelectedVoice?.Id ?? App.Settings.TtsVoice;
			var wavBytes = await ttsBackend.SynthesizeAsync(
				"You're listening to Muzsick — enjoy the music.", voice, token);

			if (token.IsCancellationRequested) return;

			if (wavBytes is not { Length: > 0 })
			{
				VoiceTestError = "Voice synthesis failed. Check the TTS model is installed.";
				VoiceTestState = VoiceTestState.Failed;
				return;
			}

			VoiceTestState = VoiceTestState.Playing;
			await audioMixer.PlayVoiceoverAsync(wavBytes, token);

			if (!token.IsCancellationRequested)
				VoiceTestState = VoiceTestState.Done;
		}
		catch (OperationCanceledException)
		{
			VoiceTestState = VoiceTestState.Idle;
		}
	}

	// ── Ollama check ─────────────────────────────────────────────────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOllamaCheckOk))]
	[NotifyPropertyChangedFor(nameof(IsOllamaModelMissing))]
	[NotifyPropertyChangedFor(nameof(IsOllamaCheckFailed))]
	[NotifyPropertyChangedFor(nameof(IsOllamaChecking))]
	[NotifyCanExecuteChangedFor(nameof(CheckOllamaCommand))]
	private OllamaCheckState _ollamaCheckState = OllamaCheckState.Idle;

	public bool IsOllamaCheckOk => OllamaCheckState == OllamaCheckState.Ok;
	public bool IsOllamaModelMissing => OllamaCheckState == OllamaCheckState.ModelMissing;
	public bool IsOllamaCheckFailed => OllamaCheckState == OllamaCheckState.Failed;
	public bool IsOllamaChecking => OllamaCheckState == OllamaCheckState.Checking;

	public string OllamaModelMissingMessage =>
		$"✗ Model \"{OllamaModel}\" not found — run: ollama pull {OllamaModel}";

	partial void OnOllamaUrlChanged(string _) => OllamaCheckState = OllamaCheckState.Idle;

	private bool CanCheckOllama() => OllamaCheckState != OllamaCheckState.Checking;

	[RelayCommand(CanExecute = nameof(CanCheckOllama))]
	private async Task CheckOllama()
	{
		OllamaCheckState = OllamaCheckState.Checking;
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			using var client = new HttpClient();
			var response = await client.GetAsync($"{OllamaUrl.TrimEnd('/')}/api/tags", cts.Token);

			if (!response.IsSuccessStatusCode)
			{
				OllamaCheckState = OllamaCheckState.Failed;
				return;
			}

			var modelToFind = OllamaModel.Trim();
			if (!modelToFind.Contains(':'))
				modelToFind += ":latest";

			var json = await response.Content.ReadAsStringAsync(cts.Token);
			var node = JsonNode.Parse(json);
			var available = node?["models"]?.AsArray()
				.Select(m => m?["name"]?.GetValue<string>() ?? "")
				.ToList() ?? [];

			OllamaCheckState = available.Any(m =>
				string.Equals(m, modelToFind, StringComparison.OrdinalIgnoreCase))
				? OllamaCheckState.Ok
				: OllamaCheckState.ModelMissing;
		}
		catch
		{
			OllamaCheckState = OllamaCheckState.Failed;
		}
	}

	// ── Claude check ─────────────────────────────────────────────────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsClaudeCheckOk))]
	[NotifyPropertyChangedFor(nameof(IsClaudeCheckFailed))]
	[NotifyPropertyChangedFor(nameof(IsClaudeChecking))]
	[NotifyCanExecuteChangedFor(nameof(CheckClaudeCommand))]
	private ClaudeCheckState _claudeCheckState = ClaudeCheckState.Idle;

	public bool IsClaudeCheckOk => ClaudeCheckState == ClaudeCheckState.Ok;
	public bool IsClaudeCheckFailed => ClaudeCheckState == ClaudeCheckState.Failed;
	public bool IsClaudeChecking => ClaudeCheckState == ClaudeCheckState.Checking;

	private bool CanCheckClaude() => ClaudeCheckState != ClaudeCheckState.Checking;

	[RelayCommand(CanExecute = nameof(CanCheckClaude))]
	private async Task CheckClaude()
	{
		ClaudeCheckState = ClaudeCheckState.Checking;
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			using var client = new HttpClient();

			var requestBody = new JsonObject
			{
				["model"] = ClaudeModel.Trim(),
				["max_tokens"] = 1,
				["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "test" }, },
			};

			using var request = new HttpRequestMessage(HttpMethod.Post,
				"https://api.anthropic.com/v1/messages");
			request.Headers.Add("x-api-key", ClaudeApiKey.Trim());
			request.Headers.Add("anthropic-version", "2023-06-01");
			request.Content = System.Net.Http.Json.JsonContent.Create(requestBody);

			var response = await client.SendAsync(request, cts.Token);
			ClaudeCheckState = response.IsSuccessStatusCode
				? ClaudeCheckState.Ok
				: ClaudeCheckState.Failed;
		}
		catch
		{
			ClaudeCheckState = ClaudeCheckState.Failed;
		}
	}

	private bool CanRefreshClaudeModels() => !IsRefreshingClaudeModels;

	[RelayCommand(CanExecute = nameof(CanRefreshClaudeModels))]
	private async Task RefreshClaudeModels()
	{
		IsRefreshingClaudeModels = true;
		ClaudeModelsRefreshError = null;
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			using var client = new HttpClient();

			using var request = new HttpRequestMessage(HttpMethod.Get,
				"https://api.anthropic.com/v1/models");
			request.Headers.Add("x-api-key", ClaudeApiKey.Trim());
			request.Headers.Add("anthropic-version", "2023-06-01");

			var response = await client.SendAsync(request, cts.Token);
			if (!response.IsSuccessStatusCode)
			{
				ClaudeModelsRefreshError =
					$"✗ Failed to fetch models ({(int)response.StatusCode}) — check your API key";
				return;
			}

			var json = await response.Content.ReadAsStringAsync(cts.Token);
			var node = JsonNode.Parse(json);
			var ids = node?["data"]?.AsArray()
				.Select(m => m?["id"]?.GetValue<string>() ?? "")
				.Where(id => id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
				.OrderBy(id => id)
				.ToList() ?? [];

			if (ids.Count == 0)
			{
				ClaudeModelsRefreshError = "✗ No Claude models returned — check your API key";
				return;
			}

			var current = ClaudeModel;
			ClaudeModels.Clear();
			foreach (var id in ids)
				ClaudeModels.Add(id);

			if (!ClaudeModels.Contains(current))
				ClaudeModels.Insert(0, current);

			if (!ClaudeModels.Contains(ClaudeModel))
				ClaudeModel = ClaudeModels[0];
		}
		catch
		{
			ClaudeModelsRefreshError = "✗ Could not reach Anthropic API — check your connection";
		}
		finally
		{
			IsRefreshingClaudeModels = false;
		}
	}

	// ── Bottom bar Preview ───────────────────────────────────────────────────

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PreviewButtonLabel))]
	[NotifyPropertyChangedFor(nameof(IsPreviewActive))]
	[NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
	[NotifyCanExecuteChangedFor(nameof(CancelPreviewCommand))]
	private PreviewState _currentPreviewState = PreviewState.Idle;

	[ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreviewError))]
	private string? _previewError;

	[ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewButtonLabel))]
	private double _previewElapsedSeconds;

	private double _previewTotalSeconds;

	public bool IsPreviewActive => CurrentPreviewState is
		PreviewState.Generating or PreviewState.Synthesising or PreviewState.Playing;

	public bool HasPreviewError => !string.IsNullOrEmpty(PreviewError);

	public string PreviewButtonLabel => CurrentPreviewState switch
	{
		PreviewState.Generating => $"Generating… {PreviewElapsedSeconds:F1}s",
		PreviewState.Synthesising => "Synthesising…",
		PreviewState.Playing => "Playing…",
		PreviewState.Done => $"Done ({_previewTotalSeconds:F1}s)",
		_ => "Preview",
	};

	private bool CanPreview() =>
		CurrentPreviewState is PreviewState.Idle or PreviewState.Done or PreviewState.Failed;

	[RelayCommand(CanExecute = nameof(CanPreview))]
	private async Task Preview()
	{
		await _previewCts.CancelAsync();
		_previewCts.Dispose();
		_previewCts = new CancellationTokenSource();
		var token = _previewCts.Token;

		PreviewError = null;
		PreviewElapsedSeconds = 0;

		try
		{
			if (!IsAiMode)
			{
				var sample = TemplateSample;
				if (string.IsNullOrWhiteSpace(sample)) return;

				CurrentPreviewState = PreviewState.Synthesising;
				var voice = SelectedVoice?.Id ?? App.Settings.TtsVoice;
				var wavBytes = await ttsBackend.SynthesizeAsync(sample, voice, token);

				if (token.IsCancellationRequested) return;

				if (wavBytes is not { Length: > 0 })
				{
					PreviewError = "Voice synthesis failed.";
					CurrentPreviewState = PreviewState.Failed;
					return;
				}

				CurrentPreviewState = PreviewState.Playing;
				await audioMixer.PlayVoiceoverAsync(wavBytes, token);

				if (!token.IsCancellationRequested)
					CurrentPreviewState = PreviewState.Done;
			}
			else
			{
				CurrentPreviewState = PreviewState.Generating;
				StartElapsedTimer();

				var sampleTrack = new TrackInfo
				{
					Title = "Bohemian Rhapsody",
					Artist = "Queen",
					Album = "A Night at the Opera",
					Year = "1975",
					Genre = "Rock",
				};

				var savedPrompt = App.Settings.AiPrompt;
				var savedUrl = App.Settings.OllamaUrl;
				var savedModel = App.Settings.OllamaModel;
				var savedAiProvider = App.Settings.AiProvider;
				var savedClaudeApiKey = App.Settings.ClaudeApiKey;
				var savedClaudeModel = App.Settings.ClaudeModel;
				App.Settings.AiPrompt = AiPrompt;
				App.Settings.OllamaUrl = OllamaUrl;
				App.Settings.OllamaModel = OllamaModel;
				App.Settings.AiProvider = AiProvider;
				App.Settings.ClaudeApiKey = ClaudeApiKey;
				App.Settings.ClaudeModel = ClaudeModel;

				ICommentaryGenerator generator = AiProvider == AiProvider.Claude
					? new ClaudeCommentaryGenerator(App.LoggerFactory?.CreateLogger<ClaudeCommentaryGenerator>())
					: new OllamaCommentaryGenerator(App.LoggerFactory?.CreateLogger<OllamaCommentaryGenerator>());

				string? commentary;
				try
				{
					commentary = await generator.GenerateAsync(sampleTrack, token);
				}
				catch (TimeoutException)
				{
					StopElapsedTimer();
					PreviewError = "AI took too long — try a simpler prompt or smaller model.";
					CurrentPreviewState = PreviewState.Failed;
					return;
				}
				catch (HttpRequestException)
				{
					StopElapsedTimer();
					PreviewError = AiProvider == AiProvider.Claude
						? "Cannot reach Claude API — check AI Provider settings."
						: "Cannot reach Ollama — check AI Provider settings.";
					CurrentPreviewState = PreviewState.Failed;
					return;
				}
				finally
				{
					App.Settings.AiPrompt = savedPrompt;
					App.Settings.OllamaUrl = savedUrl;
					App.Settings.OllamaModel = savedModel;
					App.Settings.AiProvider = savedAiProvider;
					App.Settings.ClaudeApiKey = savedClaudeApiKey;
					App.Settings.ClaudeModel = savedClaudeModel;
				}

				if (token.IsCancellationRequested) return;

				StopElapsedTimer();

				if (string.IsNullOrEmpty(commentary))
				{
					PreviewError = "AI returned an empty response. Check your prompt.";
					CurrentPreviewState = PreviewState.Failed;
					return;
				}

				CurrentPreviewState = PreviewState.Synthesising;
				var voice = SelectedVoice?.Id ?? App.Settings.TtsVoice;
				var wavBytes = await ttsBackend.SynthesizeAsync(commentary, voice, token);

				if (token.IsCancellationRequested) return;

				if (wavBytes is not { Length: > 0 })
				{
					PreviewError = "Voice synthesis failed.";
					CurrentPreviewState = PreviewState.Failed;
					return;
				}

				CurrentPreviewState = PreviewState.Playing;
				await audioMixer.PlayVoiceoverAsync(wavBytes, token);

				if (!token.IsCancellationRequested)
					CurrentPreviewState = PreviewState.Done;
			}
		}
		catch (OperationCanceledException)
		{
			StopElapsedTimer();
			CurrentPreviewState = PreviewState.Idle;
			PreviewError = null;
		}
	}

	private bool CanCancelPreview() =>
		CurrentPreviewState is PreviewState.Generating or PreviewState.Synthesising or PreviewState.Playing;

	[RelayCommand(CanExecute = nameof(CanCancelPreview))]
	private void CancelPreview()
	{
		_previewCts.Cancel();
		StopElapsedTimer();
		CurrentPreviewState = PreviewState.Idle;
		PreviewError = null;
	}

	// ── Helpers ──────────────────────────────────────────────────────────────

	public IReadOnlyList<VoiceInfo> AvailableVoices { get; } = availableVoices.Values.ToList().AsReadOnly();

	private Window? _window;
	private CancellationTokenSource _previewCts = new();
	private CancellationTokenSource _spotifyAuthCts = new();
	private readonly Stopwatch _previewStopwatch = new();
	private DispatcherTimer? _previewTimer;

	public void SetWindow(Window window)
	{
		_window = window;
		SelectedVoice = availableVoices.TryGetValue(App.Settings.TtsVoice, out var v)
			? v
			: AvailableVoices.FirstOrDefault();
		UpdateSample();
	}

	partial void OnAnnouncementTemplateChanged(string _)
	{
		UpdateSample();
		if (CurrentPreviewState is PreviewState.Done or PreviewState.Failed)
			CurrentPreviewState = PreviewState.Idle;
	}

	partial void OnAiPromptChanged(string _)
	{
		if (CurrentPreviewState is PreviewState.Done or PreviewState.Failed)
			CurrentPreviewState = PreviewState.Idle;
	}

	private void UpdateSample()
	{
		TemplateSample = string.IsNullOrWhiteSpace(AnnouncementTemplate)
			? ""
			: AnnouncementTemplateRenderer.RenderPreview(AnnouncementTemplate);
	}

	[RelayCommand]
	private void ResetTemplate() => AnnouncementTemplate = AppSettings.DefaultAnnouncementTemplate;

	[RelayCommand]
	private void ResetAiPrompt() => AiPrompt = AppSettings.DefaultAiPrompt;

	private void StartElapsedTimer()
	{
		_previewStopwatch.Restart();
		_previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
		_previewTimer.Tick += (_, _) => PreviewElapsedSeconds = _previewStopwatch.Elapsed.TotalSeconds;
		_previewTimer.Start();
	}

	private void StopElapsedTimer()
	{
		_previewTimer?.Stop();
		_previewTimer = null;
		_previewTotalSeconds = _previewStopwatch.Elapsed.TotalSeconds;
		_previewStopwatch.Stop();
	}

	/// <summary>Synchronous abort used by the window Closing event handler.</summary>
	public void AbortPreview()
	{
		_previewCts.Cancel();
		_voiceTestCts.Cancel();
		_spotifyAuthCts.Cancel();
		_previewTimer?.Stop();
		_previewTimer = null;
	}

	// ── Validation & Save ────────────────────────────────────────────────────

	private bool CanSave() => TemplateError == null && AiPromptError == null;

	[RelayCommand(CanExecute = nameof(CanSave))]
	private void Save()
	{
		if (SelectedVoice != null)
			App.Settings.TtsVoice = SelectedVoice.Id;
		App.Settings.MusicSource = MusicSource;
		App.Settings.SpotifyClientId = SpotifyClientId;
		// SpotifyRefreshToken is written directly to App.Settings during the OAuth flow
		App.Settings.AnnouncementTemplate = AnnouncementTemplate.Trim();
		App.Settings.CommentaryMode = CommentaryMode;
		App.Settings.AiPrompt = AiPrompt.Trim();
		App.Settings.OllamaUrl = OllamaUrl.Trim();
		App.Settings.OllamaModel = OllamaModel.Trim();
		App.Settings.AiProvider = AiProvider;
		App.Settings.ClaudeApiKey = ClaudeApiKey.Trim();
		App.Settings.ClaudeModel = ClaudeModel.Trim();
		SettingsManager.Save(App.Settings);
		_window?.Close(true);
	}

	[RelayCommand]
	private void Cancel()
	{
		if (isFirstRun)
		{
			if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
				lifetime.Shutdown();
		}
		else
		{
			_window?.Close(false);
		}
	}
}
