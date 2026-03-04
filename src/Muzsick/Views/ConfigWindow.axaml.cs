// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using Avalonia.Controls;
using Muzsick.ViewModels;

namespace Muzsick.Views;

public partial class ConfigWindow : Window
{
	public ConfigWindow() : this(false)
	{
	}

	public ConfigWindow(bool isFirstRun)
	{
		var vm = new ConfigWindowViewModel(isFirstRun);
		DataContext = vm;
		InitializeComponent();
		vm.SetWindow(this);
	}
}

