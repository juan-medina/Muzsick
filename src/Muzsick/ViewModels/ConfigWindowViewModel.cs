// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muzsick.Config;

namespace Muzsick.ViewModels;

public partial class ConfigWindowViewModel(bool isFirstRun) : ViewModelBase
{
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	private string _apiKey = App.Settings.LastFmApiKey;

	private Window? _window;

	public void SetWindow(Window window)
	{
		_window = window;
	}

	private bool CanSave() => !string.IsNullOrWhiteSpace(ApiKey);

	[RelayCommand(CanExecute = nameof(CanSave))]
	private void Save()
	{
		App.Settings.LastFmApiKey = ApiKey.Trim();
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

