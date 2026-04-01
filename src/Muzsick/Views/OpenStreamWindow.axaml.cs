// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Net.Http;
using Avalonia.Controls;
using Muzsick.ViewModels;

namespace Muzsick.Views;

public partial class OpenStreamWindow : Window
{
	public OpenStreamWindow(HttpClient httpClient, string? currentStreamUrl)
	{
		var vm = new OpenStreamWindowViewModel(httpClient, currentStreamUrl);
		DataContext = vm;
		InitializeComponent();
		vm.SetWindow(this);
	}

	public OpenStreamWindow() : this(new HttpClient(), null)
	{
	}
}
