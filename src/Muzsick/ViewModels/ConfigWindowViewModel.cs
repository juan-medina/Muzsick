// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
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
using Muzsick.Tts;

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


public partial class ConfigWindowViewModel(
	bool isFirstRun,
	IReadOnlyDictionary<string, VoiceInfo> availableVoices,
	ITtsBackend ttsBackend,
	AudioMixer audioMixer)
	: ViewModelBase
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ApiKeyError))]
	[NotifyPropertyChangedFor(nameof(HasApiKeyError))]
	[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	private string _apiKey = App.Settings.LastFmApiKey;

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

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(OllamaModelMissingMessage))]
	private string _ollamaModel = App.Settings.OllamaModel;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOllamaCheckOk))]
	[NotifyPropertyChangedFor(nameof(IsOllamaModelMissing))]
	[NotifyPropertyChangedFor(nameof(IsOllamaCheckFailed))]
	[NotifyPropertyChangedFor(nameof(IsOllamaChecking))]
	[NotifyCanExecuteChangedFor(nameof(CheckOllamaCommand))]
	private OllamaCheckState _ollamaCheckState = OllamaCheckState.Idle;

	[ObservableProperty] private string _templateSample = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PreviewButtonLabel))]
	[NotifyPropertyChangedFor(nameof(IsPreviewActive))]
	[NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
	[NotifyCanExecuteChangedFor(nameof(CancelPreviewCommand))]
	private PreviewState _currentPreviewState = PreviewState.Idle;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasPreviewError))]
	private string? _previewError;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PreviewButtonLabel))]
	private double _previewElapsedSeconds;

	private double _previewTotalSeconds;

	// --- Computed properties ---

	public bool IsAiMode => CommentaryMode == CommentaryMode.Ai;

	public bool IsTemplateModeSelected
	{
		get => CommentaryMode == CommentaryMode.Template;
		set { if (value) CommentaryMode = CommentaryMode.Template; }
	}

	public bool IsAiModeSelected
	{
		get => CommentaryMode == CommentaryMode.Ai;
		set { if (value) CommentaryMode = CommentaryMode.Ai; }
	}

	public bool IsPreviewActive => CurrentPreviewState is
		PreviewState.Generating or PreviewState.Synthesising or PreviewState.Playing;

	public string? ApiKeyError => string.IsNullOrWhiteSpace(ApiKey)
		? "An API key is required to load track metadata."
		: null;

	public string? TemplateError => string.IsNullOrWhiteSpace(AnnouncementTemplate)
		? "The announcement template cannot be empty."
		: null;

	public string? AiPromptError => IsAiMode && string.IsNullOrWhiteSpace(AiPrompt)
		? "An AI prompt is required when AI mode is selected."
		: null;

	public bool HasApiKeyError => ApiKeyError != null;
	public bool HasTemplateError => TemplateError != null;
	public bool HasAiPromptError => AiPromptError != null;
	public bool HasPreviewError => !string.IsNullOrEmpty(PreviewError);

	public bool IsOllamaCheckOk => OllamaCheckState == OllamaCheckState.Ok;
	public bool IsOllamaModelMissing => OllamaCheckState == OllamaCheckState.ModelMissing;
	public bool IsOllamaCheckFailed => OllamaCheckState == OllamaCheckState.Failed;
	public bool IsOllamaChecking => OllamaCheckState == OllamaCheckState.Checking;

	public string OllamaModelMissingMessage =>
		$"✗ Model \"{OllamaModel}\" not found — run: ollama pull {OllamaModel}";

	public string PreviewButtonLabel => CurrentPreviewState switch
	{
		PreviewState.Generating => $"Generating… {PreviewElapsedSeconds:F1}s",
		PreviewState.Synthesising => "Synthesising…",
		PreviewState.Playing => "Playing…",
		PreviewState.Done => $"Generated in {_previewTotalSeconds:F1}s",
		_ => "Preview",
	};

	public IReadOnlyList<VoiceInfo> AvailableVoices { get; } = availableVoices.Values.ToList().AsReadOnly();

	private Window? _window;
	private CancellationTokenSource _previewCts = new();
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

	partial void OnAnnouncementTemplateChanged(string value) => UpdateSample();

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

	partial void OnOllamaUrlChanged(string value) => OllamaCheckState = OllamaCheckState.Idle;

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

			// Normalise: "gemma3" → "gemma3:latest"
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

	// --- Preview state machine ---

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
				// Template path
				var sample = TemplateSample;
				if (string.IsNullOrWhiteSpace(sample)) return;

				CurrentPreviewState = PreviewState.Synthesising;
				var voice = SelectedVoice?.Id ?? App.Settings.TtsVoice;
				var wavBytes = await ttsBackend.SynthesizeAsync(sample, voice, token);

				if (token.IsCancellationRequested) return;

				if (wavBytes is not { Length: > 0 })
				{
					PreviewError = "Voice synthesis failed. Check the TTS model is installed.";
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
				// AI path
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

				// Temporarily apply the edited (unsaved) settings so the generator picks them up.
				var savedPrompt = App.Settings.AiPrompt;
				var savedUrl = App.Settings.OllamaUrl;
				var savedModel = App.Settings.OllamaModel;
				App.Settings.AiPrompt = AiPrompt;
				App.Settings.OllamaUrl = OllamaUrl;
				App.Settings.OllamaModel = OllamaModel;

				string? commentary;
				try
				{
					var generator = new OllamaCommentaryGenerator(App.LoggerFactory?.CreateLogger<OllamaCommentaryGenerator>());
					commentary = await generator.GenerateAsync(sampleTrack, token);
				}
				catch (TimeoutException)
				{
					StopElapsedTimer();
					PreviewError = "AI took too long to respond. Try a simpler prompt or smaller model.";
					CurrentPreviewState = PreviewState.Failed;
					return;
				}
				catch (HttpRequestException)
				{
					StopElapsedTimer();
					PreviewError = "AI unavailable — make sure Ollama is running.";
					CurrentPreviewState = PreviewState.Failed;
					return;
				}
				finally
				{
					App.Settings.AiPrompt = savedPrompt;
					App.Settings.OllamaUrl = savedUrl;
					App.Settings.OllamaModel = savedModel;
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
					PreviewError = "Voice synthesis failed. Check the TTS model is installed.";
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
			// User cancelled via the Cancel button or window closing.
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

	/// <summary>Synchronous abort used by the window Closing event handler.</summary>
	public void AbortPreview()
	{
		_previewCts.Cancel();
		_previewTimer?.Stop();
		_previewTimer = null;
	}

	// --- Validation & Save ---

	private bool CanSave() => ApiKeyError == null && TemplateError == null && AiPromptError == null;

	[RelayCommand(CanExecute = nameof(CanSave))]
	private void Save()
	{
		App.Settings.LastFmApiKey = ApiKey.Trim();
		if (SelectedVoice != null)
			App.Settings.TtsVoice = SelectedVoice.Id;
		App.Settings.AnnouncementTemplate = AnnouncementTemplate.Trim();
		App.Settings.CommentaryMode = CommentaryMode;
		App.Settings.AiPrompt = AiPrompt.Trim();
		App.Settings.OllamaUrl = OllamaUrl.Trim();
		App.Settings.OllamaModel = OllamaModel.Trim();
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
