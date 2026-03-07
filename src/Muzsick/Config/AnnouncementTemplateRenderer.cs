// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Muzsick.Metadata;

namespace Muzsick.Config;

/// <summary>
/// Renders an announcement template string using track data.
/// Tokens: {title}  {artist}  {album}  {year}  {genre}
///
/// Optional clauses:
///   [token?text]   — include the clause only when the named token is non-empty.
///   [text]         — include the clause only when ALL tokens inside are non-empty.
///
/// Examples:
///   Now playing {title} by {artist}[year?, released in {year}]
///   Now playing {title} by {artist}[album? from {album}][year? ({year})]
///
/// Token names are case-insensitive.
/// </summary>
public static partial class AnnouncementTemplateRenderer
{
	public const string DefaultTemplate = "Now playing {title} by {artist}[year?, released in {year}]";

	private static readonly IReadOnlyDictionary<string, string> _previewValues =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["title"] = "Bohemian Rhapsody",
			["artist"] = "Queen",
			["album"] = "A Night at the Opera",
			["year"] = "1975",
			["genre"] = "Rock",
		};

	public static string Render(string template, TrackInfo track)
	{
		var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["title"] = track.Title,
			["artist"] = track.Artist,
			["album"] = track.Album,
			["year"] = track.Year ?? "",
			["genre"] = track.Genre ?? "",
		};

		return Resolve(template, tokens);
	}

	public static string RenderPreview(string template)
	{
		return Resolve(template, new Dictionary<string, string>(
			_previewValues, StringComparer.OrdinalIgnoreCase));
	}

	private static string Resolve(string template, Dictionary<string, string> tokens)
	{
		var result = OptionalClausePattern().Replace(template, m =>
		{
			var guard = m.Groups[1].Value; // token name before '?', or empty when [...] form

			if (!string.IsNullOrEmpty(guard))
			{
				// [token?clause] — show only when the guard token is non-empty
				var clause = m.Groups[2].Value;
				if (!tokens.TryGetValue(guard, out var guardVal) || string.IsNullOrEmpty(guardVal))
					return "";

				return ReplaceTokens(clause, tokens);
			}
			else
			{
				// [clause] — show only when every token inside is non-empty
				var clause = m.Groups[3].Value;
				if (HasEmptyToken(clause, tokens))
					return "";

				return ReplaceTokens(clause, tokens);
			}
		});

		return ReplaceTokens(result, tokens).Trim();
	}

	private static string ReplaceTokens(string text, Dictionary<string, string> tokens)
	{
		return TokenPattern().Replace(text, m =>
		{
			var key = m.Groups[1].Value;
			return tokens.TryGetValue(key, out var val) ? val : m.Value;
		});
	}

	private static bool HasEmptyToken(string clause, Dictionary<string, string> tokens)
	{
		foreach (Match m in TokenPattern().Matches(clause))
		{
			var key = m.Groups[1].Value;
			if (tokens.TryGetValue(key, out var val) && string.IsNullOrEmpty(val))
				return true;
		}

		return false;
	}

	// Matches [token?clause] or [clause].
	// Group 1 = guard token name (may be empty), Group 2 = clause text.
	[GeneratedRegex(@"\[(\w+)\?([^\[\]]*)\]|\[([^\[\]]*)\]", RegexOptions.IgnoreCase)]
	private static partial Regex OptionalClausePattern();

	[GeneratedRegex(@"\{(\w+)\}", RegexOptions.IgnoreCase)]
	private static partial Regex TokenPattern();
}
