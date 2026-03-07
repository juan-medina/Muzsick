// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Avalonia.Controls;
using Muzsick.Audio;
using Muzsick.Tts;
using Muzsick.ViewModels;

namespace Muzsick.Views;

public partial class ConfigWindow : Window
{
	public ConfigWindow(
		bool isFirstRun,
		IReadOnlyDictionary<string, VoiceInfo> availableVoices,
		ITtsBackend ttsBackend,
		AudioMixer audioMixer)
	{
		var vm = new ConfigWindowViewModel(isFirstRun, availableVoices, ttsBackend, audioMixer);
		DataContext = vm;
		InitializeComponent();
		vm.SetWindow(this);
		Closing += (_, _) => vm.CancelPreview();
	}

	public ConfigWindow() : this(false, new Dictionary<string, VoiceInfo>(),
		new KokoroTtsBackend(), new AudioMixer(null))
	{
	}
}
