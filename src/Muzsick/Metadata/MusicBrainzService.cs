// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Muzsick.Metadata;

/// <summary>
/// Stub implementation of IMusicBrainzService.
/// Returns the original track and a bare ArtistInfo unchanged until
/// MusicBrainz integration is implemented in V1.
/// </summary>
public class MusicBrainzService(ILogger? logger = null) : IMusicBrainzService
{
	public Task<(TrackInfo Track, ArtistInfo Artist)> EnrichAsync(TrackInfo track)
	{
		logger?.LogDebug(
			"EnrichAsync called for '{Title}' by '{Artist}' - not yet implemented",
			track.Title, track.Artist);

		var artist = new ArtistInfo { Name = track.Artist };

		return Task.FromResult((track, artist));
	}
}
