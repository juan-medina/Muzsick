// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
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
	public static bool MissingApiKeyError { get; set; }

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

			if (MissingApiKeyError)
			{
				var errorWindow = new Window
				{
					Title = "Muzsick — Missing API Key",
					Width = 460,
					Height = 150,
					WindowStartupLocation = WindowStartupLocation.CenterScreen,
					CanResize = false,
				};

				var okButton = new Button
				{
					Content = "OK",
					HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
					Margin = new Thickness(0, 8, 0, 0),
				};
				okButton.Click += (_, _) => errorWindow.Close();

				errorWindow.Content = new StackPanel
				{
					Margin = new Thickness(20),
					Children =
					{
						new TextBlock
						{
							Text =
								"Last.fm API key is not set.\n\n" +
								$"Edit:  {SettingsManager.SettingsPath}\n\n" +
								"Get a free key at:  https://www.last.fm/api/account/create",
							TextWrapping = Avalonia.Media.TextWrapping.Wrap,
						},
						okButton,
					}
				};

				desktop.MainWindow = errorWindow;
			}
			else
			{
				desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
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
