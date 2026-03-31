// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Muzsick.Audio;

/// <summary>
/// Resolves an arbitrary user input — direct stream URL, remote playlist URL, or local playlist file —
/// to a bare stream URL and a human-readable name.
/// </summary>
public static class PlaylistResolver
{
	private static readonly string[] _playlistExtensions = [".pls", ".m3u", ".m3u8"];

	/// <summary>
	/// Resolves the input to a (StreamUrl, StreamName) pair.
	/// Returns null if the input cannot be resolved to a playable stream URL.
	/// </summary>
	public static async Task<(string StreamUrl, string StreamName)?> ResolveAsync(
		string input,
		HttpClient httpClient,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(input))
			return null;

		input = input.Trim();

		// Local file path
		if (File.Exists(input))
		{
			var content = await File.ReadAllTextAsync(input, cancellationToken);
			var ext = Path.GetExtension(input).ToLowerInvariant();
			return ParsePlaylistContent(content, ext, fallbackName: Path.GetFileNameWithoutExtension(input));
		}

		if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
			return null;

		if (uri.Scheme is not "http" and not "https")
			return null;

		// Remote playlist URL
		if (_playlistExtensions.Any(ext => input.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
		{
			try
			{
				var response = await httpClient.GetAsync(input, cancellationToken);
				if (!response.IsSuccessStatusCode)
					return null;

				var content = await response.Content.ReadAsStringAsync(cancellationToken);
				var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
				return ParsePlaylistContent(content, ext, fallbackName: uri.Host);
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
			{
				return null;
			}
		}

		// Direct stream URL — name comes from hostname
		var name = uri.Host;
		return (input, name);
	}

	private static (string StreamUrl, string StreamName)? ParsePlaylistContent(
		string content,
		string extension,
		string fallbackName)
	{
		return extension is ".pls"
			? ParsePls(content, fallbackName)
			: ParseM3U(content, fallbackName);
	}

	private static (string StreamUrl, string StreamName)? ParsePls(string content, string fallbackName)
	{
		string? streamUrl = null;
		string? streamName = null;

		foreach (var line in content.Split('\n'))
		{
			var trimmed = line.Trim();

			if (trimmed.StartsWith("File1=", StringComparison.OrdinalIgnoreCase))
				streamUrl = trimmed.Substring("File1=".Length).Trim();
			else if (trimmed.StartsWith("Title1=", StringComparison.OrdinalIgnoreCase))
				streamName = trimmed.Substring("Title1=".Length).Trim();
		}

		if (string.IsNullOrEmpty(streamUrl))
			return null;

		return (streamUrl, string.IsNullOrEmpty(streamName) ? fallbackName : streamName);
	}

	private static (string StreamUrl, string StreamName)? ParseM3U(string content, string fallbackName)
	{
		string? streamUrl = null;
		string? streamName = null;

		foreach (var line in content.Split('\n'))
		{
			var trimmed = line.Trim();

			if (string.IsNullOrEmpty(trimmed))
				continue;

			// #EXTINF:-1,Station Name
			if (trimmed.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
			{
				var comma = trimmed.IndexOf(',');
				if (comma >= 0 && comma < trimmed.Length - 1)
					streamName = trimmed.Substring(comma + 1).Trim();
				continue;
			}

			if (trimmed.StartsWith('#'))
				continue;

			// First non-comment line is the stream URL
			streamUrl = trimmed;
			break;
		}

		if (string.IsNullOrEmpty(streamUrl))
			return null;

		return (streamUrl, string.IsNullOrEmpty(streamName) ? fallbackName : streamName);
	}
}
