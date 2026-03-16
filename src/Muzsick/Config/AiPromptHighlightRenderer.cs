// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Text.RegularExpressions;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Muzsick.Config;

/// <summary>
/// Colours AI prompt syntax in the AI prompt editor.
/// Lines starting with # — grey/green (comments, stripped before sending to the AI)
/// {context} — orange (the only valid token; replaced with track metadata at runtime)
/// {anything else} — red (not a recognised token; will not be substituted)
/// </summary>
public partial class AiPromptHighlightRenderer : DocumentColorizingTransformer
{
	private static readonly IBrush _commentBrush = new SolidColorBrush(Color.Parse("#6A9955"));
	private static readonly IBrush _tokenBrush = new SolidColorBrush(Color.Parse("#FF6B35"));
	private static readonly IBrush _invalidBrush = new SolidColorBrush(Color.Parse("#F44747"));

	protected override void ColorizeLine(DocumentLine line)
	{
		var text = CurrentContext.Document.GetText(line);
		var offset = line.Offset;

		// Comment lines — colour the entire line and skip token scanning
		if (text.TrimStart().StartsWith('#'))
		{
			ChangeLinePart(offset, offset + line.Length,
				element => element.TextRunProperties.SetForegroundBrush(_commentBrush));
			return;
		}

		foreach (Match m in TokenPattern().Matches(text))
		{
			var brush = m.Groups[1].Value.Equals("context", StringComparison.OrdinalIgnoreCase)
				? _tokenBrush
				: _invalidBrush;

			ChangeLinePart(
				offset + m.Index,
				offset + m.Index + m.Length,
				element => element.TextRunProperties.SetForegroundBrush(brush));
		}
	}

	// {tokenname} — group 1 = name
	[GeneratedRegex(@"\{(\w+)\}", RegexOptions.IgnoreCase)]
	private static partial Regex TokenPattern();
}
