// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using Avalonia.Controls;
using Muzsick.ViewModels;

namespace Muzsick.Views;

public partial class AboutWindow : Window
{
	public AboutWindow()
	{
		var vm = new AboutWindowViewModel();
		DataContext = vm;
		InitializeComponent();
		vm.SetWindow(this);
	}
}
