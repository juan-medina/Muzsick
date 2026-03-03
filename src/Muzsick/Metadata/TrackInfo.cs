// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

namespace Muzsick.Metadata;

public class TrackInfo
{
	// Core fields — populated by StreamPlayer via ICY metadata
	public string Title { get; init; } = "";
	public string Artist { get; init; } = "";
	public string Album { get; init; } = "";

	// Enrichment fields — populated by IMusicBrainzService
	public string? Year { get; init; }
	public string? Genre { get; init; }
	public string? CoverArtUrl { get; init; }
	public string? MusicBrainzId { get; init; }
}
