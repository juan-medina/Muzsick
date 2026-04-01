// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using Avalonia.Media.Imaging;

namespace Muzsick.ViewModels;

/// <summary>
/// A single entry in the session play history, captured after metadata enrichment and TTS synthesis.
/// </summary>
public sealed class HistoryEntry
{
	public required string Title { get; init; }
	public required string Artist { get; init; }
	public string? Album { get; init; }
	public string? Year { get; init; }

	public string? TrackLastFmUrl { get; init; }
	public string? ArtistLastFmUrl { get; init; }
	public string? AlbumLastFmUrl { get; init; }

	/// <summary>Album art thumbnail — null when no cover art was available.</summary>
	public Bitmap? AlbumArt { get; init; }

	/// <summary>Synthesised voiceover WAV — null when TTS was skipped or failed.</summary>
	public byte[]? AnnouncementWav { get; init; }

	public string AlbumLine =>
		Album is { Length: > 0 } a
			? Year is { Length: > 0 } y ? $"{a} ({y})" : a
			: Year is { Length: > 0 } y2
				? y2
				: "";
}
