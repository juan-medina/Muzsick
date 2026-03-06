// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muzsick.Config;
using Muzsick.Tts;

namespace Muzsick.ViewModels;

public partial class ConfigWindowViewModel(bool isFirstRun, IReadOnlyDictionary<string, VoiceInfo> availableVoices)
	: ViewModelBase
{
	[ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	private string _apiKey = App.Settings.LastFmApiKey;

	[ObservableProperty] private VoiceInfo? _selectedVoice;

	public IReadOnlyList<VoiceInfo> AvailableVoices { get; } = availableVoices.Values.ToList().AsReadOnly();

	private Window? _window;

	public void SetWindow(Window window)
	{
		_window = window;
		SelectedVoice = availableVoices.TryGetValue(App.Settings.TtsVoice, out var v)
			? v
			: AvailableVoices.FirstOrDefault();
	}

	private bool CanSave() => !string.IsNullOrWhiteSpace(ApiKey);

	[RelayCommand(CanExecute = nameof(CanSave))]
	private void Save()
	{
		App.Settings.LastFmApiKey = ApiKey.Trim();
		if (SelectedVoice != null)
			App.Settings.TtsVoice = SelectedVoice.Id;
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
