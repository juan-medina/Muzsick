// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MetaBrainz.MusicBrainz;
using Microsoft.Extensions.Logging;

namespace Muzsick.Metadata;

/// <summary>
/// Resolves an artist portrait image URL from Wikidata.
/// Accepts either a MusicBrainz artist MBID or a direct Wikidata URL.
/// Flow: MBID → MusicBrainz url-rels → Wikidata QID → Wikidata API (P18) → Wikimedia Commons thumbnail URL.
/// </summary>
public class WikidataArtistService(HttpClient httpClient, ILogger? logger = null)
{
	/// <summary>
	/// Resolves an artist image URL from a MusicBrainz artist MBID.
	/// Looks up the artist's Wikidata relationship via MusicBrainz, then fetches the image from Wikidata.
	/// Returns <c>null</c> when no image is available or any step of the lookup fails.
	/// </summary>
	public async Task<string?> FetchImageUrlFromMbidAsync(string artistMbid)
	{
		try
		{
			using var query = new Query("Muzsick", "0.1", "https://github.com/juan-medina/muzsick");
			var artist = await query.LookupArtistAsync(new Guid(artistMbid), Include.UrlRelationships);

			if (artist.Relationships == null)
				return null;

			string? wikidataUrl = null;
			foreach (var rel in artist.Relationships)
			{
				if (rel.Type == "wikidata" && rel.Url?.Resource != null)
				{
					wikidataUrl = rel.Url.Resource.ToString();
					break;
				}
			}

			if (wikidataUrl == null)
				return null;

			return await FetchImageUrlFromWikidataUrlAsync(wikidataUrl);
		}
		catch (Exception ex)
		{
			logger?.LogWarning("Metadata: artist image fetch failed for MBID {Mbid} - {Message}", artistMbid,
				ex.Message);
			return null;
		}
	}

	/// <summary>
	/// Resolves an artist image URL from a Wikidata entity URL (e.g. https://www.wikidata.org/wiki/Q1299).
	/// Useful when the caller already has a Wikidata URL — e.g. from Last.fm's artist.getInfo response.
	/// Returns <c>null</c> when no image is available or the lookup fails.
	/// </summary>
	private async Task<string?> FetchImageUrlFromWikidataUrlAsync(string wikidataUrl)
	{
		try
		{
			var qid = wikidataUrl.TrimEnd('/').Split('/')[^1];
			if (!qid.StartsWith("Q", StringComparison.OrdinalIgnoreCase))
				return null;

			logger?.LogDebug("Metadata: fetching artist image for QID {Qid}", qid);

			var apiUrl =
				$"https://www.wikidata.org/w/api.php?action=wbgetclaims&entity={qid}&property=P18&format=json";

			var response = await httpClient.GetAsync(apiUrl);
			if (!response.IsSuccessStatusCode)
				return null;

			var json = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("claims", out var claims))
				return null;

			if (!claims.TryGetProperty("P18", out var p18))
			{
				logger?.LogDebug("Metadata: no P18 image claim found for QID {Qid}", qid);
				return null;
			}

			if (p18.GetArrayLength() == 0)
				return null;

			var filename = p18[0]
				.GetProperty("mainsnak")
				.GetProperty("datavalue")
				.GetProperty("value")
				.GetString();

			if (string.IsNullOrEmpty(filename))
				return null;

			var imageUrl = BuildWikimediaUrl(filename, 250);
			logger?.LogDebug("Metadata: artist image URL '{Url}'", imageUrl);
			return imageUrl;
		}
		catch (Exception ex)
		{
			logger?.LogWarning("Metadata: artist image fetch failed - {Message}", ex.Message);
			return null;
		}
	}

	/// <summary>
	/// Builds a Wikimedia Commons thumbnail URL for a given filename and width.
	/// Wikimedia uses an MD5-based path: first char / first two chars / filename.
	/// </summary>
	private static string BuildWikimediaUrl(string filename, int width)
	{
		var normalised = filename.Replace(' ', '_');

		var hash = System.Security.Cryptography.MD5.HashData(
			System.Text.Encoding.UTF8.GetBytes(normalised));
		var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

		var a = hex[0];
		var ab = hex[..2];

		return
			$"https://upload.wikimedia.org/wikipedia/commons/thumb/{a}/{ab}/{Uri.EscapeDataString(normalised)}/{width}px-{Uri.EscapeDataString(normalised)}";
	}
}
