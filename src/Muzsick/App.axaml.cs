// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using Muzsick.Config;
using Muzsick.ViewModels;
using Muzsick.Views;
using System.Linq;

namespace Muzsick;

public class App : Application
{
	public static ILoggerFactory? LoggerFactory { get; set; }
	public static AppSettings Settings { get; set; } = new();

	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			// Avoid duplicate validations from both Avalonia and the CommunityToolkit.
			// More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
			DisableAvaloniaDataAnnotationValidation();

			var mainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
			desktop.MainWindow = mainWindow;

			if (string.IsNullOrWhiteSpace(Settings.LastFmApiKey))
			{
				mainWindow.Show();
				if (mainWindow.DataContext is MainWindowViewModel vm)
				{
					var configWindow = new ConfigWindow(
						isFirstRun: true,
						vm.TtsAvailableVoices,
						vm.TtsBackend,
						vm.AudioMixer);
					configWindow.ShowDialog(mainWindow);
				}
			}
		}

		base.OnFrameworkInitializationCompleted();
	}

	private void DisableAvaloniaDataAnnotationValidation()
	{
		var dataValidationPluginsToRemove =
			BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

		foreach (var plugin in dataValidationPluginsToRemove)
		{
			BindingPlugins.DataValidators.Remove(plugin);
		}
	}
}
