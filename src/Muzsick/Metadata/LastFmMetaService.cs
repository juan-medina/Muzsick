// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Muzsick.Metadata;

public partial class LastFmMetaService : IMetaService, IDisposable
{
	private const string _baseUrl = "https://ws.audioscrobbler.com/2.0/";
	private static readonly string _apiKey = LoadApiKey();
	private const int _maxRetries = 3;
	private static readonly TimeSpan _retryBaseDelay = TimeSpan.FromMilliseconds(500);
	private static readonly TimeSpan _minRequestInterval = TimeSpan.FromMilliseconds(200);
	private static readonly TimeSpan _cacheTtl = TimeSpan.FromHours(24);

	private readonly ILogger? _logger;
	private readonly HttpClient _httpClient;
	private readonly WikidataArtistService _wikidata;

	private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
	private readonly SemaphoreSlim _rateLimiter = new(1, 1);
	private DateTime _lastRequestTime = DateTime.MinValue;

	private static string LoadApiKey()
	{
		try
		{
			using var stream = Assembly.GetExecutingAssembly()
				.GetManifestResourceStream("Muzsick.ApiKeys.json");
			if (stream == null) return "";
			using var reader = new StreamReader(stream);
			using var doc = JsonDocument.Parse(reader.ReadToEnd());
			return doc.RootElement.GetProperty("LastFm").GetString() ?? "";
		}
		catch
		{
			return "";
		}
	}

	public LastFmMetaService(ILogger? logger = null)
	{
		_logger = logger;

		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Muzsick/0.1 (https://github.com/juan-medina/muzsick)");

		_wikidata = new WikidataArtistService(_httpClient, logger);

		_ = WarmUpAsync();
	}


	private async Task WarmUpAsync()
	{
		try
		{
			await _httpClient.GetAsync("https://ws.audioscrobbler.com/", HttpCompletionOption.ResponseHeadersRead);
			_logger?.LogInformation("Metadata: connection to Last.fm established");
		}
		catch
		{
			// Silently ignore.
		}
	}


	/// <summary>
	/// Enriches a track using a multi-stage lookup strategy to handle the unreliable nature
	/// of ICY metadata from radio stations.
	///
	/// Lookup stages (each only runs if the previous returned null or a non-rich result):
	///
	///   1. Exact ICY artist  + exact ICY title       (autocorrect on, compilation check)
	///   2. Primary artist    + exact ICY title        (splits "A, B, C" → "A")
	///   3. Exact ICY artist  + cleaned title          (strips feat./remix/version decorators)
	///   4. Primary artist    + cleaned title
	///   5. track.search fallback                      (handles censored/altered station titles)
	///        → takes top result from same artist, retries getInfo with its real name
	///
	/// "Rich" means the result has at least a cover art URL or an album name.
	/// A non-rich result (MBID only, no art) is kept as a fallback so the artist
	/// image can still be resolved even when no rich track result exists.
	///
	/// Compilation detection: if track.getInfo returns album.artist = "Various Artists"
	/// the call is retried with autocorrect=0 to force an exact artist match.
	/// </summary>
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

		var primaryArtist = track.Artist.Contains(',')
			? track.Artist.Split(',')[0].Trim()
			: track.Artist;
		var cleanedTitle = CleanTitle(track.Title);

		// Try all candidate combinations. Continue to the next attempt when the current
		// result is null or not rich (matched but no album/cover art).
		var entry = await FetchEntryAsync(track.Artist, track.Title);

		if (entry == null || !entry.IsRich)
		{
			if (primaryArtist != track.Artist)
				entry = await FetchEntryAsync(primaryArtist, track.Title) ?? entry;
		}

		if (entry == null || !entry.IsRich)
		{
			if (cleanedTitle != track.Title)
				entry = await FetchEntryAsync(track.Artist, cleanedTitle) ?? entry;
		}

		if (entry == null || !entry.IsRich)
		{
			if (primaryArtist != track.Artist && cleanedTitle != track.Title)
				entry = await FetchEntryAsync(primaryArtist, cleanedTitle) ?? entry;
		}

		// Last resort: track.search fuzzy-matches even censored/altered titles.
		// Take the top result from the same artist and retry getInfo with its real name.
		if (entry == null || !entry.IsRich)
		{
			var correctedTitle = await SearchCorrectedTitleAsync(primaryArtist, track.Title);
			if (correctedTitle != null && correctedTitle != track.Title && correctedTitle != cleanedTitle)
			{
				_logger?.LogDebug(
					"Metadata: search suggested corrected title '{Corrected}' for '{Original}'",
					correctedTitle, track.Title);
				entry = await FetchEntryAsync(primaryArtist, correctedTitle) ?? entry;
			}
		}

		if (entry == null || !entry.IsRich)
		{
			_logger?.LogInformation("Metadata: no match found for '{Title}' by '{Artist}'", track.Title, track.Artist);
			_cache[cacheKey] = CacheEntry.Empty();
			return (track, new ArtistInfo { Name = track.Artist });
		}

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
	/// Fetches a cache entry for the given artist and title. If the result is a Various Artists
	/// compilation (autocorrect drifted to the wrong release), retries without autocorrect to
	/// get the artist's own release instead.
	/// </summary>
	private async Task<CacheEntry?> FetchEntryAsync(string artist, string title)
	{
		var entry = await FetchEntryInternalAsync(artist, title, autocorrect: true);

		if (entry?.IsCompilation == true)
		{
			_logger?.LogDebug(
				"Metadata: compilation result for '{Title}' by '{Artist}', retrying without autocorrect",
				title, artist);
			entry = await FetchEntryInternalAsync(artist, title, autocorrect: false) ?? entry;
		}

		return entry;
	}


	private async Task<CacheEntry?> FetchEntryInternalAsync(string artist, string title, bool autocorrect)
	{
		var url =
			$"{_baseUrl}?method=track.getInfo&api_key={_apiKey}" +
			$"&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}" +
			$"&format=json&autocorrect={(autocorrect ? "1" : "0")}";

		for (var attempt = 1; attempt <= _maxRetries; attempt++)
		{
			try
			{
				await ThrottleAsync();

				var response = await _httpClient.GetAsync(url);
				if (!response.IsSuccessStatusCode)
					return null;

				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				// Last.fm returns { "error": 6, "message": "Track not found" } on misses.
				// Definitive — no point retrying.
				if (root.TryGetProperty("error", out _))
					return null;

				if (!root.TryGetProperty("track", out var trackEl))
					return null;

				var entry = new CacheEntry { CreatedAt = DateTime.UtcNow };

				// Album name, cover art, and compilation flag
				if (trackEl.TryGetProperty("album", out var albumEl))
				{
					entry.Album = albumEl.TryGetProperty("title", out var albumTitle)
						? albumTitle.GetString()
						: null;

					// Detect compilations — when the album artist is "Various Artists" autocorrect
					// has drifted away from the actual artist's release.
					if (albumEl.TryGetProperty("artist", out var albumArtistEl))
					{
						var albumArtist = albumArtistEl.GetString() ?? "";
						entry.IsCompilation = albumArtist.Equals("Various Artists",
							StringComparison.OrdinalIgnoreCase);
					}

					// Last.fm returns images ordered small → extralarge — pick the last (largest).
					if (albumEl.TryGetProperty("image", out var images) && images.GetArrayLength() > 0)
					{
						var lastImage = images[images.GetArrayLength() - 1];
						if (lastImage.TryGetProperty("#text", out var imgText))
						{
							var imgUrl = imgText.GetString();
							if (!string.IsNullOrWhiteSpace(imgUrl))
								entry.CoverArtUrl = imgUrl;
						}
					}
				}

				// Release year from wiki published date — e.g. "26 May 1977, 00:00"
				if (trackEl.TryGetProperty("wiki", out var wiki) &&
				    wiki.TryGetProperty("published", out var published))
				{
					var publishedStr = published.GetString() ?? "";
					var parts = publishedStr.Split(',');
					if (parts.Length > 0)
					{
						var dateParts = parts[0].Trim().Split(' ');
						if (dateParts.Length == 3)
							entry.Year = dateParts[2];
					}
				}

				// Top tag as genre
				if (trackEl.TryGetProperty("toptags", out var toptags) &&
				    toptags.TryGetProperty("tag", out var tags) &&
				    tags.GetArrayLength() > 0)
				{
					if (tags[0].TryGetProperty("name", out var tagName))
						entry.Genre = tagName.GetString();
				}

				// Artist MBID for Wikidata image lookup.
				// track.getInfo sometimes omits the MBID for smaller artists — fall back to
				// artist.getInfo by name to retrieve it.
				if (trackEl.TryGetProperty("artist", out var artistEl))
				{
					var mbid = artistEl.TryGetProperty("mbid", out var mbidEl)
						? mbidEl.GetString()
						: null;

					var artistName = artistEl.TryGetProperty("name", out var nameEl)
						? nameEl.GetString()
						: null;

					if (!string.IsNullOrWhiteSpace(artistName))
						entry.ArtistName = artistName;

					if (string.IsNullOrWhiteSpace(mbid) && !string.IsNullOrWhiteSpace(artistName))
						mbid = await FetchArtistMbidAsync(artistName);

					if (!string.IsNullOrWhiteSpace(mbid))
					{
						entry.ArtistMbid = mbid;
						entry.ArtistImageUrl = await _wikidata.FetchImageUrlFromMbidAsync(mbid);
					}
				}

				// Track name from Last.fm — may be corrected relative to the ICY title.
				if (trackEl.TryGetProperty("name", out var trackNameEl))
				{
					var trackName = trackNameEl.GetString();
					if (!string.IsNullOrWhiteSpace(trackName))
						entry.TrackName = trackName;
				}

				return entry;
			}
			catch (Exception ex) when (attempt < _maxRetries)
			{
				var delay = _retryBaseDelay * attempt;
				_logger?.LogDebug(
					"Metadata: Last.fm lookup failed (attempt {Attempt}/{Max}), retrying in {Delay}ms - {Message}",
					attempt, _maxRetries, delay.TotalMilliseconds, ex.Message);
				await Task.Delay(delay);
			}
			catch (Exception ex)
			{
				_logger?.LogWarning("Metadata: Last.fm lookup failed - {Message}", ex.Message);
				return null;
			}
		}

		return null;
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


	/// <summary>
	/// Calls artist.getInfo by name to retrieve the MusicBrainz artist MBID.
	/// Used as a fallback when track.getInfo omits the MBID for smaller artists.
	/// Returns null when the artist is not found or has no MBID on Last.fm.
	/// </summary>
	private async Task<string?> FetchArtistMbidAsync(string artistName)
	{
		var url =
			$"{_baseUrl}?method=artist.getInfo&api_key={_apiKey}" +
			$"&artist={Uri.EscapeDataString(artistName)}&format=json&autocorrect=1";

		try
		{
			await ThrottleAsync();

			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode)
				return null;

			var json = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			if (root.TryGetProperty("error", out _))
				return null;

			if (!root.TryGetProperty("artist", out var artistEl))
				return null;

			if (!artistEl.TryGetProperty("mbid", out var mbidEl))
				return null;

			var mbid = mbidEl.GetString();
			if (string.IsNullOrWhiteSpace(mbid))
				return null;

			_logger?.LogDebug("Metadata: resolved MBID {Mbid} for artist '{Artist}' via artist.getInfo", mbid,
				artistName);
			return mbid;
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("Metadata: artist.getInfo failed for '{Artist}' - {Message}", artistName, ex.Message);
			return null;
		}
	}


	/// <summary>
	/// Calls track.search to find the real track name when getInfo fails — handles censored or
	/// slightly altered titles from radio stations. Returns the top result's name only when it
	/// belongs to the same artist, null otherwise.
	/// </summary>
	private async Task<string?> SearchCorrectedTitleAsync(string artist, string title)
	{
		var url =
			$"{_baseUrl}?method=track.search&api_key={_apiKey}" +
			$"&track={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}" +
			$"&format=json&limit=3";

		try
		{
			await ThrottleAsync();

			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode)
				return null;

			var json = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			if (!root.TryGetProperty("results", out var results))
				return null;

			if (!results.TryGetProperty("trackmatches", out var matches))
				return null;

			if (!matches.TryGetProperty("track", out var tracks) || tracks.GetArrayLength() == 0)
				return null;

			var top = tracks[0];

			// Only accept the suggestion if it's from the same artist.
			if (!top.TryGetProperty("artist", out var resultArtist))
				return null;

			if (!resultArtist.GetString()?.Equals(artist, StringComparison.OrdinalIgnoreCase) == true)
				return null;

			if (!top.TryGetProperty("name", out var name))
				return null;

			return name.GetString();
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("Metadata: track.search failed - {Message}", ex.Message);
			return null;
		}
	}


	private static (TrackInfo Track, ArtistInfo Artist) BuildResult(TrackInfo original, CacheEntry entry)
	{
		var enriched = new TrackInfo
		{
			Title = !string.IsNullOrWhiteSpace(entry.TrackName) ? entry.TrackName : original.Title,
			Artist = !string.IsNullOrWhiteSpace(entry.ArtistName) ? entry.ArtistName : original.Artist,
			Album = !string.IsNullOrEmpty(original.Album) ? original.Album : entry.Album ?? "",
			Year = entry.Year,
			Genre = entry.Genre,
			CoverArtUrl = entry.CoverArtUrl,
		};

		var artist = new ArtistInfo
		{
			Name = enriched.Artist, ImageUrl = entry.ArtistImageUrl, MusicBrainzId = entry.ArtistMbid,
		};

		return (enriched, artist);
	}


	private static string CleanTitle(string title)
	{
		var cleaned = FeaturedArtistPattern().Replace(title, "");
		cleaned = ParentheticalSuffixPattern().Replace(cleaned, "");
		cleaned = DashSuffixPattern().Replace(cleaned, "");
		cleaned = BracketSuffixPattern().Replace(cleaned, "");
		return cleaned.Trim();
	}


	[GeneratedRegex(@"\s*[\(\[](?:from|original|official|motion picture|soundtrack)[^\)\]]*[\)\]]",
		RegexOptions.IgnoreCase)]
	private static partial Regex BracketSuffixPattern();

	[GeneratedRegex(@"\s+(ft\.?|feat\.?|featuring)\s+.+$", RegexOptions.IgnoreCase)]
	private static partial Regex FeaturedArtistPattern();

	[GeneratedRegex(
		@"\s*[\(\[](radio edit|album version|remaster(ed)?|live|acoustic|extended|instrumental|remix|edit|version|mix)[\)\]]",
		RegexOptions.IgnoreCase)]
	private static partial Regex ParentheticalSuffixPattern();

	[GeneratedRegex(@"\s+-\s+.*(radio edit|remix|edit|version|mix|remaster(ed)?).*$", RegexOptions.IgnoreCase)]
	private static partial Regex DashSuffixPattern();


	public void Dispose() => _httpClient.Dispose();


	private sealed class CacheEntry
	{
		public DateTime CreatedAt { get; init; }
		public bool IsExpired => DateTime.UtcNow - CreatedAt > _cacheTtl;

		// A result is only worth keeping if it has at least album art or an album name.
		public bool IsRich => CoverArtUrl != null || Album != null;
		public bool IsCompilation { get; set; }

		public string? TrackName { get; set; }
		public string? ArtistName { get; set; }
		public string? ArtistMbid { get; set; }
		public string? ArtistImageUrl { get; set; }
		public string? CoverArtUrl { get; set; }
		public string? Album { get; set; }
		public string? Year { get; set; }
		public string? Genre { get; set; }

		public static CacheEntry Empty() => new() { CreatedAt = DateTime.UtcNow };
	}
}
