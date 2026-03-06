// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Avalonia.Controls;
using Muzsick.Tts;
using Muzsick.ViewModels;

namespace Muzsick.Views;

public partial class ConfigWindow : Window
{
	public ConfigWindow(bool isFirstRun, IReadOnlyDictionary<string, VoiceInfo> availableVoices)
	{
		var vm = new ConfigWindowViewModel(isFirstRun, availableVoices);
		DataContext = vm;
		InitializeComponent();
		vm.SetWindow(this);
	}

	public ConfigWindow() : this(false, new Dictionary<string, VoiceInfo>())
	{
	}
}
