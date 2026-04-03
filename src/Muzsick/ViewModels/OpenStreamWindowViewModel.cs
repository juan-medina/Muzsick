// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muzsick.Audio;
using Muzsick.Config;

namespace Muzsick.ViewModels;

public enum OpenStreamState
{
	Idle,
	Resolving,
	Error,
}

public partial class OpenStreamWindowViewModel : ViewModelBase
{
	private readonly HttpClient _httpClient;
	private Window? _window;

	[ObservableProperty] [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
	private string _urlInput = "";

	[ObservableProperty] private OpenStreamState _state = OpenStreamState.Idle;
	[ObservableProperty] private string _errorMessage = "";

	public bool IsResolving => State == OpenStreamState.Resolving;
	public bool HasError => State == OpenStreamState.Error;

	public OpenStreamWindowViewModel(HttpClient httpClient, string? currentStreamUrl)
	{
		_httpClient = httpClient;
		_urlInput = currentStreamUrl ?? "";
	}

	public void SetWindow(Window window) => _window = window;

	[RelayCommand(CanExecute = nameof(CanOpen))]
	private async Task Open()
	{
		State = OpenStreamState.Resolving;
		ErrorMessage = "";

		var result = await PlaylistResolver.ResolveAsync(UrlInput, _httpClient, CancellationToken.None);

		if (result == null)
		{
			ErrorMessage = "Could not resolve a stream URL. Check the input and try again.";
			State = OpenStreamState.Error;
			return;
		}

		_window?.Close(result);
	}

	private bool CanOpen() => !string.IsNullOrWhiteSpace(UrlInput);

	[RelayCommand]
	private void Cancel() => _window?.Close(null);

	[RelayCommand]
	private async Task Browse()
	{
		if (_window == null) return;

		var filter = new FilePickerFileType("Playlist Files")
		{
			Patterns = ["*.pls", "*.m3u", "*.m3u8"],
			MimeTypes = ["audio/x-scpls", "audio/x-mpegurl", "application/vnd.apple.mpegurl"],
		};

		var settings = SettingsManager.Load() ?? new AppSettings();

		// First run: default to bundled playlists folder. Otherwise use last directory.
		var startDir = settings.LastBrowseDirectory != null && Directory.Exists(settings.LastBrowseDirectory)
			? settings.LastBrowseDirectory
			: Path.Combine(AppContext.BaseDirectory, "Playlists");

		IStorageFolder? suggestedFolder = null;
		if (Directory.Exists(startDir))
			suggestedFolder = await _window.StorageProvider.TryGetFolderFromPathAsync(startDir);

		var result = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Select playlist file",
			AllowMultiple = false,
			FileTypeFilter = [filter],
			SuggestedStartLocation = suggestedFolder,
		});

		if (result.Count > 0)
		{
			var path = result[0].Path.LocalPath;
			UrlInput = path;
			State = OpenStreamState.Idle;
			ErrorMessage = "";

			settings.LastBrowseDirectory = Path.GetDirectoryName(path);
			SettingsManager.Save(settings);
		}
	}

	partial void OnStateChanged(OpenStreamState value)
	{
		OnPropertyChanged(nameof(IsResolving));
		OnPropertyChanged(nameof(HasError));
	}
}
