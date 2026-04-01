// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muzsick.Audio;

namespace Muzsick.ViewModels;

public partial class HistoryWindowViewModel(AudioMixer audioMixer) : ViewModelBase
{
	public ObservableCollection<HistoryEntry> Entries { get; } = [];

	private Window? _window;
	private CancellationTokenSource _voiceoverCts = new();

	public void SetWindow(Window window) => _window = window;

	[RelayCommand]
	private async Task ReplayDj(HistoryEntry entry)
	{
		if (entry.AnnouncementWav is not { Length: > 0 }) return;

		var cts = new CancellationTokenSource();
		var previous = Interlocked.Exchange(ref _voiceoverCts, cts);
		await previous.CancelAsync();
		previous.Dispose();

		await audioMixer.PlayVoiceoverAsync(entry.AnnouncementWav, cts.Token);
	}

	[RelayCommand]
	private void OpenLastFm(string? url)
	{
		if (string.IsNullOrEmpty(url)) return;
		try
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			App.LoggerFactory?.CreateLogger("HistoryWindowViewModel")
				.LogWarning("Failed to open Last.fm URL: {Message}", ex.Message);
		}
	}

	[RelayCommand]
	private void Close() => _window?.Close();
}
