// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

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

	[RelayCommand]
	private void PlayPause()
	{
		IsPlaying = !IsPlaying;
	}

	[RelayCommand]
	private void BrowsePlaylist()
	{
		// Placeholder for file browser logic
	}
}
