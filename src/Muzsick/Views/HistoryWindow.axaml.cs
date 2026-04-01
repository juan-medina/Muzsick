// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using Avalonia.Controls;
using Muzsick.Audio;
using Muzsick.ViewModels;

namespace Muzsick.Views;

public partial class HistoryWindow : Window
{
	public HistoryWindow(HistoryWindowViewModel vm)
	{
		DataContext = vm;
		InitializeComponent();
		vm.SetWindow(this);
	}

	public HistoryWindow() : this(new HistoryWindowViewModel(new AudioMixer(null)))
	{
	}
}
