// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;

namespace Muzsick.Metadata;

public interface IMusicBrainzService
{
	/// <summary>
	/// Uses artist name AND track title together to unambiguously identify the recording,
	/// then returns enriched track and artist data.
	/// Returns originals unchanged if nothing is found.
	/// </summary>
	Task<(TrackInfo Track, ArtistInfo Artist)> EnrichAsync(TrackInfo track);
}
