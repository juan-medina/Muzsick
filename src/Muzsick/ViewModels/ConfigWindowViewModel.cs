// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muzsick.Audio;
using Muzsick.Config;
using Muzsick.Tts;

namespace Muzsick.ViewModels;

public partial class ConfigWindowViewModel(
	bool isFirstRun,
	IReadOnlyDictionary<string, VoiceInfo> availableVoices,
	ITtsBackend ttsBackend,
	AudioMixer audioMixer)
	: ViewModelBase
{
	[ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	private string _apiKey = App.Settings.LastFmApiKey;

	[ObservableProperty] private VoiceInfo? _selectedVoice;

	[ObservableProperty] private string _announcementTemplate = App.Settings.AnnouncementTemplate;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SpeakPreviewCommand))]
	private string _templateSample = "";

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SpeakPreviewCommand))]
	private bool _isTtsPreviewing;

	public IReadOnlyList<VoiceInfo> AvailableVoices { get; } = availableVoices.Values.ToList().AsReadOnly();

	private Window? _window;
	private CancellationTokenSource _previewCts = new();

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
	private void ResetTemplate() => AnnouncementTemplate = AnnouncementTemplateRenderer.DefaultTemplate;

	private bool CanSpeakPreview() => !IsTtsPreviewing && !string.IsNullOrWhiteSpace(TemplateSample);

	[RelayCommand(CanExecute = nameof(CanSpeakPreview))]
	private async Task SpeakPreview()
	{
		await _previewCts.CancelAsync();
		_previewCts.Dispose();
		_previewCts = new CancellationTokenSource();
		var token = _previewCts.Token;

		IsTtsPreviewing = true;
		try
		{
			var voice = SelectedVoice?.Id ?? App.Settings.TtsVoice;
			var wavBytes = await ttsBackend.SynthesizeAsync(TemplateSample, voice, token);

			if (!token.IsCancellationRequested && wavBytes is { Length: > 0 })
				await audioMixer.PlayVoiceoverAsync(wavBytes, token);
		}
		catch (TaskCanceledException) { }
		finally
		{
			IsTtsPreviewing = false;
		}
	}

	private bool CanSave() => !string.IsNullOrWhiteSpace(ApiKey);

	[RelayCommand(CanExecute = nameof(CanSave))]
	private void Save()
	{
		App.Settings.LastFmApiKey = ApiKey.Trim();
		if (SelectedVoice != null)
			App.Settings.TtsVoice = SelectedVoice.Id;
		App.Settings.AnnouncementTemplate = AnnouncementTemplate.Trim();
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

	public void CancelPreview()
	{
		_previewCts.Cancel();
	}
}
