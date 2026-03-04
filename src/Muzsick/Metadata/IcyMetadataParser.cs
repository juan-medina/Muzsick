// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace Muzsick.Metadata;

/// <summary>
/// Extracts artist and title from raw ICY metadata strings produced by internet radio streams.
/// </summary>
public class IcyMetadataParser(ILogger? logger = null)
{
	/// <summary>
	/// Reads the VLC <see cref="Media"/> metadata fields and returns a <see cref="TrackInfo"/>,
	/// or <c>null</c> when no meaningful track data is present.
	/// </summary>
	public TrackInfo? ExtractTrackInfo(Media media)
	{
		var title = media.Meta(MetadataType.Title) ?? "";
		var artist = media.Meta(MetadataType.Artist) ?? "";
		var album = media.Meta(MetadataType.Album) ?? "";
		var nowPlaying = media.Meta(MetadataType.NowPlaying) ?? "";
		var description = media.Meta(MetadataType.Description) ?? "";
		var genre = media.Meta(MetadataType.Genre) ?? "";

		logger?.LogDebug("Raw metadata - Title: '{Title}', Artist: '{Artist}', Album: '{Album}'", title, artist, album);
		logger?.LogDebug("Raw metadata - NowPlaying: '{NowPlaying}', Description: '{Description}', Genre: '{Genre}'",
			nowPlaying, description, genre);

		if (IsStationNoise(title))
		{
			logger?.LogDebug("Skipping station name, looking for actual track info");
			title = "";
		}

		if (!string.IsNullOrEmpty(nowPlaying) && !IsStationNoise(nowPlaying))
		{
			logger?.LogDebug("Processing NowPlaying metadata: '{NowPlaying}'", nowPlaying);
			Parse(nowPlaying, out var parsedArtist, out var parsedTitle);

			if (!string.IsNullOrEmpty(parsedArtist) || !string.IsNullOrEmpty(parsedTitle))
			{
				artist = parsedArtist;
				title = parsedTitle;
				logger?.LogDebug("Parsed ICY from NowPlaying - Artist: '{Artist}', Title: '{Title}'", artist, title);
			}
			else if (string.IsNullOrEmpty(title))
			{
				title = nowPlaying;
				logger?.LogDebug("Using NowPlaying as title: '{Title}'", title);
			}
		}

		if (!string.IsNullOrEmpty(title) && !IsStationNoise(title) && string.IsNullOrEmpty(artist) &&
		    title.Contains(" - "))
		{
			Parse(title, out var parsedArtist, out var parsedTitle);
			if (!string.IsNullOrEmpty(parsedArtist))
			{
				artist = parsedArtist;
				title = parsedTitle;
				logger?.LogDebug("Parsed ICY from Title - Artist: '{Artist}', Title: '{Title}'", artist, title);
			}
		}

		var hasMeaningfulData = (!string.IsNullOrEmpty(title) && !IsStationNoise(title)) ||
		                        !string.IsNullOrEmpty(artist);

		if (!hasMeaningfulData)
		{
			logger?.LogDebug("No meaningful track metadata found");
			return null;
		}

		return new TrackInfo
		{
			Title = title, Artist = string.IsNullOrEmpty(artist) ? "Unknown Artist" : artist, Album = album
		};
	}

	/// <summary>
	/// Splits a raw ICY string into artist and title components.
	/// Handles the common formats: "Artist - Title", "Artist: Title", "Title by Artist".
	/// When no separator is found the whole string is returned as the title.
	/// </summary>
	private static void Parse(string icyString, out string artist, out string title)
	{
		artist = "";
		title = "";

		if (string.IsNullOrEmpty(icyString)) return;

		var separators = new[] { " - ", " – ", " — ", ": ", " by " };

		foreach (var separator in separators)
		{
			var parts = icyString.Split([separator], 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2) continue;

			if (separator == " by ")
			{
				title = parts[0].Trim();
				artist = parts[1].Trim();
			}
			else
			{
				artist = parts[0].Trim();
				title = parts[1].Trim();
			}

			return;
		}

		// No separator found — treat the whole string as title
		title = icyString.Trim();
	}

	/// <summary>
	/// Returns true when a metadata value looks like a station identification frame rather than
	/// a real track title. VLC sometimes surfaces the stream hostname (e.g. "truckers.fm",
	/// "radio.example.com") as the Title field before real ICY data arrives.
	/// A domain-like string is defined as: contains a dot, no whitespace, and no digits-only
	/// segments that would indicate a normal song title.
	/// </summary>
	private static bool IsStationNoise(string value)
	{
		if (string.IsNullOrWhiteSpace(value)) return false;

		// Must contain a dot to look like a domain
		if (!value.Contains('.')) return false;

		// Must not contain spaces — real titles almost always do
		if (value.Contains(' ')) return false;

		return true;
	}
}
