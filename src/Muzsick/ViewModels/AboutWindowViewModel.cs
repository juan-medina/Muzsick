// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Reflection;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace Muzsick.ViewModels;

public partial class AboutWindowViewModel : ViewModelBase
{
	public string AppVersion { get; }

	private Window? _window;

	public AboutWindowViewModel()
	{
		var v = Assembly.GetExecutingAssembly().GetName().Version;
		AppVersion = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v0";
	}

	public void SetWindow(Window window) => _window = window;

	[RelayCommand]
	private void Close() => _window?.Close();
}
