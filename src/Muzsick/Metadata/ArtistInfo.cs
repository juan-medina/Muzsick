// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

namespace Muzsick.Metadata;

public class ArtistInfo
{
	// Core fields — always populated
	public string Name { get; init; } = "";

	// Enrichment fields — populated by IMetaService
	public string? ImageUrl { get; init; }
	public string? Bio { get; init; }
	public string? MusicBrainzId { get; init; }
}
