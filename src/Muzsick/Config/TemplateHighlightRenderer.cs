// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Muzsick.Config;

/// <summary>
/// Colours template syntax in the announcement template editor.
///
/// For [year?, released in {year}]:
///   [year?,      — teal   (opening bracket + guard + operator)
///    released in — white  (plain clause text)
///   {year}       — orange (token inside clause)
///   ]            — teal   (closing bracket)
///
/// Standalone {token}  — orange if valid, red if unknown
/// Malformed  [clause] — fully red if guard token is unknown
/// </summary>
public partial class TemplateHighlightRenderer : DocumentColorizingTransformer
{
	private static readonly IBrush _tokenBrush = new SolidColorBrush(Color.Parse("#FF6B35"));
	private static readonly IBrush _clauseBrush = new SolidColorBrush(Color.Parse("#4EC9B0"));
	private static readonly IBrush _normalBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
	private static readonly IBrush _invalidBrush = new SolidColorBrush(Color.Parse("#F44747"));

	private static readonly HashSet<string> _validTokens =
		new(System.StringComparer.OrdinalIgnoreCase)
		{
			"title",
			"artist",
			"album",
			"year",
			"genre"
		};

	protected override void ColorizeLine(DocumentLine line)
	{
		var text = CurrentContext.Document.GetText(line);
		var offset = line.Offset;

		// Colour clauses first, then tokens on top for anything inside clause content
		foreach (Match m in ClausePattern().Matches(text))
			ColouriseClause(m, offset, text);

		// Colour standalone tokens (those not already inside a clause)
		foreach (Match m in TokenPattern().Matches(text))
		{
			if (!IsInsideClause(m.Index, text))
				ColouriseToken(m, offset);
		}
	}

	private void ColouriseClause(Match m, int offset, string text)
	{
		// Group 1 = guard token (e.g. "year"), Group 2 = clause body after "?,"
		// Group 3 = bare clause body for [...] form (no guard)
		var guard = m.Groups[1].Value;
		var guardValid = string.IsNullOrEmpty(guard) || _validTokens.Contains(guard);

		if (!guardValid)
		{
			// Entire clause red for unknown guard
			Colourise(offset + m.Index, offset + m.Index + m.Length, _invalidBrush);
			return;
		}

		var clauseStart = m.Index;
		var clauseEnd = m.Index + m.Length;
		var clauseBody = string.IsNullOrEmpty(guard) ? m.Groups[3].Value : m.Groups[2].Value;
		var bodyStartIdx = string.IsNullOrEmpty(guard)
			? m.Index + 1 // [ + body
			: m.Index + 1 + guard.Length + 2; // [ + guard + ?,

		// Opening: "[guard?," or just "["
		var openLen = bodyStartIdx - clauseStart;
		Colourise(offset + clauseStart, offset + clauseStart + openLen, _clauseBrush);

		// Clause body — white base, then tokens on top
		Colourise(offset + bodyStartIdx, offset + bodyStartIdx + clauseBody.Length, _normalBrush);
		foreach (Match tm in TokenPattern().Matches(clauseBody))
			Colourise(offset + bodyStartIdx + tm.Index,
				offset + bodyStartIdx + tm.Index + tm.Length,
				_validTokens.Contains(tm.Groups[1].Value) ? _tokenBrush : _invalidBrush);

		// Closing "]"
		Colourise(offset + clauseEnd - 1, offset + clauseEnd, _clauseBrush);
	}

	private void ColouriseToken(Match m, int offset)
	{
		var brush = _validTokens.Contains(m.Groups[1].Value) ? _tokenBrush : _invalidBrush;
		Colourise(offset + m.Index, offset + m.Index + m.Length, brush);
	}

	// Returns true if the character at index is inside a [...] clause
	private static bool IsInsideClause(int index, string text)
	{
		foreach (Match m in ClausePattern().Matches(text))
		{
			if (index >= m.Index && index < m.Index + m.Length)
				return true;
		}

		return false;
	}

	private void Colourise(int start, int end, IBrush brush)
	{
		if (start >= end) return;
		ChangeLinePart(start, end, element =>
			element.TextRunProperties.SetForegroundBrush(brush));
	}

	// [guard?,body] — group 1 = guard, group 2 = body
	// [body]        — group 3 = body
	[GeneratedRegex(@"\[(\w+)\?,([^\[\]]*)\]|\[([^\[\]]*)\]", RegexOptions.IgnoreCase)]
	private static partial Regex ClausePattern();

	// {tokenname} — group 1 = name
	[GeneratedRegex(@"\{(\w+)\}", RegexOptions.IgnoreCase)]
	private static partial Regex TokenPattern();
}
