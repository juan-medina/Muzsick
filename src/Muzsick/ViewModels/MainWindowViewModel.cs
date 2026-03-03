// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Muzsick.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
	[ObservableProperty]
	private string songTitle = "No track loaded";

	[ObservableProperty]
	private string artistName = "Unknown artist";

	[ObservableProperty]
	private string albumName = "Unknown album";

	[ObservableProperty]
	private bool isPlaying = false;

	[ObservableProperty]
	private string? playlistPath = null;

	private Window? _mainWindow;

	public void SetMainWindow(Window window)
	{
		_mainWindow = window;
	}

	[RelayCommand]
	private void PlayPause()
	{
		IsPlaying = !IsPlaying;
	}

	[RelayCommand]
	private async Task BrowsePlaylist()
	{
		if (_mainWindow == null) return;

		// Create file type filters for playlist files
		var fileTypeFilter = new FilePickerFileType("Playlist Files")
		{
			Patterns = new[] { "*.pls", "*.m3u", "*.m3u8" },
			MimeTypes = new[] { "audio/x-scpls", "audio/x-mpegurl", "application/vnd.apple.mpegurl" }
		};

		// Configure the file picker options
		var options = new FilePickerOpenOptions
		{
			Title = "Select Radio Playlist",
			AllowMultiple = false,
			FileTypeFilter = new[] { fileTypeFilter }
		};

		// Show the file picker dialog
		var result = await _mainWindow.StorageProvider.OpenFilePickerAsync(options);

		if (result.Count > 0)
		{
			var selectedFile = result[0];
			PlaylistPath = selectedFile.Path.LocalPath;

			// Update the UI to show the selected file
			SongTitle = $"Playlist: {System.IO.Path.GetFileNameWithoutExtension(PlaylistPath)}";
			ArtistName = "Ready to play";
			AlbumName = PlaylistPath;
		}
	}
}
