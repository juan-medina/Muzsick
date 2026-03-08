// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using Muzsick.Audio;
using Muzsick.Config;
using Muzsick.Tts;
using Muzsick.ViewModels;

namespace Muzsick.Views;

public partial class ConfigWindow : Window
{
	private readonly TextEditor? _templateEditor;

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

		_templateEditor = this.FindControl<TextEditor>("TemplateEditor");
		if (_templateEditor != null)
		{
			_templateEditor.AttachedToVisualTree += (_, _) =>
			{
				_templateEditor.TextArea.Foreground = Brushes.White;
				_templateEditor.TextArea.TextView.LineTransformers.Add(new TemplateHighlightRenderer());

				_templateEditor.Text = vm.AnnouncementTemplate;

				_templateEditor.TextChanged += (_, _) =>
				{
					if (vm.AnnouncementTemplate != _templateEditor.Text)
						vm.AnnouncementTemplate = _templateEditor.Text;
				};

				vm.PropertyChanged += (_, e) =>
				{
					if (e.PropertyName == nameof(vm.AnnouncementTemplate)
					    && _templateEditor.Text != vm.AnnouncementTemplate)
						_templateEditor.Text = vm.AnnouncementTemplate;
				};
			};
		}
	}

	public ConfigWindow() : this(false, new Dictionary<string, VoiceInfo>(),
		new KokoroTtsBackend(), new AudioMixer(null))
	{
	}
}
