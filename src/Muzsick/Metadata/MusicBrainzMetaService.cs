// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using Microsoft.Extensions.Logging;

namespace Muzsick.Metadata;

public partial class MusicBrainzMetaService : IMetaService, IDisposable
{
	private readonly ILogger? _logger;
	private readonly Query _query;
	private readonly HttpClient _httpClient;

	private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
	private static readonly TimeSpan _cacheTtl = TimeSpan.FromHours(24);

	private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
	private static DateTime _lastRequestTime = DateTime.MinValue;
	private static readonly TimeSpan _minRequestInterval = TimeSpan.FromMilliseconds(1100);

	public MusicBrainzMetaService(ILogger? logger = null)
	{
		_logger = logger;

		_query = new Query("Muzsick", "0.1", "https://github.com/juan-medina/muzsick");
		Query.DelayBetweenRequests = 0;

		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Muzsick/0.1 (https://github.com/juan-medina/muzsick)");

		_ = WarmUpAsync();
	}


	private async Task WarmUpAsync()
	{
		try
		{
			await _httpClient.GetAsync("https://musicbrainz.org/", HttpCompletionOption.ResponseHeadersRead);
			_logger?.LogInformation("Metadata: connection to MusicBrainz established");
		}
		catch
		{
			// Silently ignore.
		}
	}


	public async Task<(TrackInfo Track, ArtistInfo Artist)> EnrichAsync(TrackInfo track)
	{
		if (string.IsNullOrWhiteSpace(track.Artist) ||
		    string.IsNullOrWhiteSpace(track.Title) ||
		    track.Artist == "Unknown Artist")
		{
			return (track, new ArtistInfo { Name = track.Artist });
		}

		var cacheKey = $"{track.Artist}|{track.Title}".ToLowerInvariant();

		if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
		{
			_logger?.LogInformation("Metadata: cache hit for '{Title}' by '{Artist}'", track.Title, track.Artist);
			return BuildResult(track, cached);
		}

		_logger?.LogInformation("Metadata: looking up '{Title}' by '{Artist}'", track.Title, track.Artist);

		var recording = await FindBestRecordingAsync(track.Artist, track.Title);

		// If artist contains multiple artists (comma-separated ICY format),
		// retry with just the first one — MusicBrainz uses featured artist credits,
		// not comma-joined names.
		if (recording == null && track.Artist.Contains(','))
		{
			var primaryArtist = track.Artist.Split(',')[0].Trim();
			_logger?.LogInformation("Metadata: retrying with primary artist '{PrimaryArtist}'", primaryArtist);
			recording = await FindBestRecordingAsync(primaryArtist, track.Title);
		}

		if (recording == null)
		{
			_logger?.LogInformation("Metadata: no match found for '{Title}' by '{Artist}'", track.Title, track.Artist);
			_cache[cacheKey] = CacheEntry.Empty();
			return (track, new ArtistInfo { Name = track.Artist });
		}

		var entry = await BuildCacheEntryAsync(recording);
		_cache[cacheKey] = entry;

		_logger?.LogInformation(
			"Metadata: enriched '{Title}' - album '{Album}', year {Year}, cover art {CoverArt}, artist image {ArtistImage}",
			track.Title,
			entry.Album ?? "unknown",
			entry.Year ?? "unknown",
			entry.CoverArtUrl != null ? "found" : "not found",
			entry.ArtistImageUrl != null ? "found" : "not found");

		return BuildResult(track, entry);
	}


	/// <summary>
	/// Searches for a recording by exact title, then also tries a cleaned title if the title
	/// differs. Returns whichever result scores higher (prefers studio albums over compilations).
	/// This means decorated titles like "Sandstorm - Radio Edit" will find the original single
	/// even when the exact title lands only on a compilation.
	/// </summary>
	private async Task<IRecording?> FindBestRecordingAsync(string artist, string title)
	{
		var exact = await FindRecordingAsync(artist, title);

		var cleanedTitle = CleanTitle(title);
		if (cleanedTitle == title)
			return exact;

		_logger?.LogDebug("Metadata: also trying cleaned title '{CleanedTitle}'", cleanedTitle);
		var cleaned = await FindRecordingAsync(artist, cleanedTitle);

		if (exact == null) return cleaned;
		if (cleaned == null) return exact;

		return ScoreRecording(cleaned) > ScoreRecording(exact) ? cleaned : exact;
	}


	private async Task<IRecording?> FindRecordingAsync(string artist, string title)
	{
		try
		{
			var luceneQuery = $"recording:\"{EscapeLucene(title)}\" AND artist:\"{EscapeLucene(artist)}\"";
			await ThrottleAsync();

			var results = await _query.FindRecordingsAsync(luceneQuery, limit: 5);
			if (results.Results.Count == 0)
				return null;

			// Search results don't include release group type data — do a full
			// lookup on the top result to get releases with release group info.
			Guid? id = results.Results[0].Item.Id;

			await ThrottleAsync();
			return await _query.LookupRecordingAsync(id.Value,
				Include.Releases | Include.ReleaseGroups | Include.ArtistCredits | Include.Genres);
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("Metadata: MusicBrainz search failed - {Message}", ex.Message);
			return null;
		}
	}


	private async Task<CacheEntry> BuildCacheEntryAsync(IRecording recording)
	{
		var entry = new CacheEntry { CreatedAt = DateTime.UtcNow };

		if (recording.Releases != null)
		{
			IRelease? bestRelease = null;
			var bestScore = -1;

			foreach (var release in recording.Releases)
			{
				var score = ScoreRelease(release);
				var primaryType = release.ReleaseGroup?.PrimaryType ?? "null";
				var secondaryTypes = release.ReleaseGroup?.SecondaryTypes != null
					? string.Join(",", release.ReleaseGroup.SecondaryTypes)
					: "none";
				_logger?.LogDebug(
					"Metadata: release '{Title}' year={Year} type={Type} secondary={Secondary} score={Score}",
					release.Title,
					release.Date?.Year?.ToString() ?? "none",
					primaryType,
					secondaryTypes,
					score);

				if (score > bestScore)
				{
					bestScore = score;
					bestRelease = release;
				}
			}

			if (bestRelease != null)
			{
				entry.Album = bestRelease.Title;
				entry.ReleaseId = bestRelease.Id.ToString();
				entry.ReleaseGroupId = bestRelease.ReleaseGroup?.Id.ToString();

				if (bestRelease.Date?.Year is { } year)
					entry.Year = year.ToString();
			}
		}

		entry.RecordingId = recording.Id.ToString();

		if (recording.ArtistCredit is { Count: > 0 })
		{
			var credit = recording.ArtistCredit[0];
			entry.ArtistMbid = credit.Artist?.Id.ToString();
		}

		if (recording.Genres is { Count: > 0 })
			entry.Genre = recording.Genres[0].Name;

		// Try release cover art first, then fall back to release group.
		if (!string.IsNullOrEmpty(entry.ReleaseId))
			entry.CoverArtUrl = await ResolveRedirectUrlAsync($"release/{entry.ReleaseId}");

		if (entry.CoverArtUrl == null && !string.IsNullOrEmpty(entry.ReleaseGroupId))
			entry.CoverArtUrl = await ResolveRedirectUrlAsync($"release-group/{entry.ReleaseGroupId}");

		// Fetch artist image from Wikidata if we have an artist MBID.
		if (!string.IsNullOrEmpty(entry.ArtistMbid))
			entry.ArtistImageUrl = await FetchArtistImageUrlAsync(entry.ArtistMbid);

		return entry;
	}


	/// <summary>
	/// Scores a release so we can pick the most appropriate one.
	/// Higher is better. Studio albums with dates score highest.
	/// Singles score higher than compilations so that e.g. "Sandstorm"
	/// prefers its original single release over a 90s compilation.
	/// </summary>
	private static int ScoreRelease(IRelease release)
	{
		var score = 0;

		if (release.Date?.Year != null)
			score += 10;

		var primaryType = release.ReleaseGroup?.PrimaryType ?? "";
		score += primaryType switch
		{
			"Album" => 20,
			"EP" => 10,
			"Single" => 12,
			_ => 0
		};

		if (release.ReleaseGroup?.SecondaryTypes == null) return score;
		foreach (var secondary in release.ReleaseGroup.SecondaryTypes)
		{
			if (secondary is "Compilation" or "Live" or "Remix" or "Soundtrack" or "DJ-mix")
				score -= 15;
		}

		return score;
	}


	/// <summary>
	/// Returns the best release score for a recording, used to compare two candidate recordings.
	/// </summary>
	private static int ScoreRecording(IRecording? recording)
	{
		if (recording?.Releases == null) return -1;
		var best = -1;
		foreach (var release in recording.Releases)
		{
			var s = ScoreRelease(release);
			if (s > best) best = s;
		}
		return best;
	}


	/// <summary>
	/// Fetches the artist image URL from Wikidata via the MusicBrainz artist's Wikidata relationship.
	/// Flow: MusicBrainz artist (with url-rels) -> Wikidata QID -> Wikidata API -> image filename -> Wikimedia URL.
	/// </summary>
	private async Task<string?> FetchArtistImageUrlAsync(string artistMbid)
	{
		try
		{
			await ThrottleAsync();
			var artist = await _query.LookupArtistAsync(new Guid(artistMbid), Include.UrlRelationships);

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

			var qid = wikidataUrl.TrimEnd('/').Split('/')[^1];
			if (!qid.StartsWith("Q", StringComparison.OrdinalIgnoreCase))
				return null;

			_logger?.LogDebug("Metadata: fetching artist image for QID {Qid}", qid);

			var wikidataApiUrl =
				$"https://www.wikidata.org/w/api.php?action=wbgetclaims&entity={qid}&property=P18&format=json";

			var response = await _httpClient.GetAsync(wikidataApiUrl);
			if (!response.IsSuccessStatusCode)
				return null;

			var json = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("claims", out var claims))
				return null;
			if (!claims.TryGetProperty("P18", out var p18))
			{
				_logger?.LogDebug("Metadata: no P18 image claim found for QID {Qid}", qid);
				return null;
			}
			if (p18.GetArrayLength() == 0)
				return null;

			var datavalue = p18[0]
				.GetProperty("mainsnak")
				.GetProperty("datavalue")
				.GetProperty("value");

			var filename = datavalue.GetString();
			if (string.IsNullOrEmpty(filename))
				return null;

			var imageUrl = BuildWikimediaUrl(filename, 250);
			_logger?.LogDebug("Metadata: artist image URL '{Url}'", imageUrl);
			return imageUrl;
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("Metadata: artist image fetch failed - {Message}", ex.Message);
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

		return $"https://upload.wikimedia.org/wikipedia/commons/thumb/{a}/{ab}/{Uri.EscapeDataString(normalised)}/{width}px-{Uri.EscapeDataString(normalised)}";
	}


	private async Task<string?> ResolveRedirectUrlAsync(string coverArtPath)
	{
		try
		{
			await ThrottleAsync();

			var url = $"https://coverartarchive.org/{coverArtPath}/front-250";
			var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

			if (!response.IsSuccessStatusCode)
				return null;

			return response.RequestMessage?.RequestUri?.ToString();
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("Metadata: cover art fetch failed - {Message}", ex.Message);
			return null;
		}
	}


	private static (TrackInfo Track, ArtistInfo Artist) BuildResult(TrackInfo original, CacheEntry entry)
	{
		var enriched = new TrackInfo
		{
			Title = original.Title,
			Artist = original.Artist,
			Album = !string.IsNullOrEmpty(original.Album) ? original.Album : entry.Album ?? "",
			Year = entry.Year,
			Genre = entry.Genre,
			CoverArtUrl = entry.CoverArtUrl,
			MusicBrainzId = entry.RecordingId,
		};

		var artist = new ArtistInfo
		{
			Name = original.Artist,
			ImageUrl = entry.ArtistImageUrl,
			MusicBrainzId = entry.ArtistMbid,
		};

		return (enriched, artist);
	}


	private async Task ThrottleAsync()
	{
		await _rateLimiter.WaitAsync();
		try
		{
			var elapsed = DateTime.UtcNow - _lastRequestTime;
			var remaining = _minRequestInterval - elapsed;
			if (remaining > TimeSpan.Zero)
				await Task.Delay(remaining);
			_lastRequestTime = DateTime.UtcNow;
		}
		finally
		{
			_rateLimiter.Release();
		}
	}


	private static string CleanTitle(string title)
	{
		var cleaned = FeaturedArtistPattern().Replace(title, "");
		cleaned = ParentheticalSuffixPattern().Replace(cleaned, "");
		cleaned = DashSuffixPattern().Replace(cleaned, "");
		cleaned = BracketSuffixPattern().Replace(cleaned, "");
		return cleaned.Trim();
	}

	private static string EscapeLucene(string value)
		=> value.Replace("\"", "\\\"").Replace(".", "\\.");


	[GeneratedRegex(@"\s*[\(\[](?:from|original|official|motion picture|soundtrack)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
	private static partial Regex BracketSuffixPattern();

	[GeneratedRegex(@"\s+(ft\.?|feat\.?|featuring)\s+.+$", RegexOptions.IgnoreCase)]
	private static partial Regex FeaturedArtistPattern();

	[GeneratedRegex(
		@"\s*[\(\[](radio edit|album version|remaster(ed)?|live|acoustic|extended|instrumental|remix|edit|version|mix)[\)\]]",
		RegexOptions.IgnoreCase)]
	private static partial Regex ParentheticalSuffixPattern();

	[GeneratedRegex(@"\s+-\s+.*(radio edit|remix|edit|version|mix|remaster(ed)?).*$", RegexOptions.IgnoreCase)]
	private static partial Regex DashSuffixPattern();


	public void Dispose()
	{
		_query.Dispose();
		_httpClient.Dispose();
	}


	private sealed class CacheEntry
	{
		public DateTime CreatedAt { get; init; }
		public bool IsExpired => DateTime.UtcNow - CreatedAt > _cacheTtl;

		public string? RecordingId { get; set; }
		public string? ArtistMbid { get; set; }
		public string? ArtistImageUrl { get; set; }
		public string? CoverArtUrl { get; set; }
		public string? ReleaseId { get; set; }
		public string? ReleaseGroupId { get; set; }
		public string? Album { get; set; }
		public string? Year { get; set; }
		public string? Genre { get; set; }

		public static CacheEntry Empty()
			=> new() { CreatedAt = DateTime.UtcNow };
	}
}
