// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using Avalonia.Controls;
using Muzsick.ViewModels;

namespace Muzsick.Views;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		InitializeComponent();

		if (DataContext is MainWindowViewModel viewModel)
			viewModel.SetMainWindow(this);

		DataContextChanged += (_, _) =>
		{
			if (DataContext is MainWindowViewModel vm)
				vm.SetMainWindow(this);
		};

		Closing += (_, _) =>
		{
			if (DataContext is IDisposable disposable)
				disposable.Dispose();
		};
	}
}
