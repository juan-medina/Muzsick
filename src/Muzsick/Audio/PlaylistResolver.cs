// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PlaylistsNET.Content;

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

		// Local file
		if (File.Exists(input))
		{
			var ext = Path.GetExtension(input).ToLowerInvariant();
			await using var stream = File.OpenRead(input);
			return ParsePlaylist(stream, ext, fallbackName: Path.GetFileNameWithoutExtension(input));
		}

		if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
			return null;

		if (uri.Scheme is not "http" and not "https")
			return null;

		// Remote playlist URL
		var inputExt = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
		if (Array.Exists(_playlistExtensions, e => e == inputExt))
		{
			try
			{
				var response = await httpClient.GetAsync(input, cancellationToken);
				if (!response.IsSuccessStatusCode)
					return null;

				await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
				return ParsePlaylist(stream, inputExt, fallbackName: uri.Host);
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
			{
				return null;
			}
		}

		// Direct stream URL
		return (input, uri.Host);
	}

	private static (string StreamUrl, string StreamName)? ParsePlaylist(
		Stream stream,
		string extension,
		string fallbackName)
	{
		try
		{
			var parser = PlaylistParserFactory.GetPlaylistParser(extension);
			if (parser == null) return null;

			var playlist = parser.GetFromStream(stream);
			var paths = playlist.GetTracksPaths();
			if (paths.Count == 0) return null;

			var title = playlist switch
			{
				PlaylistsNET.Models.PlsPlaylist pls when pls.PlaylistEntries.Count > 0
					=> pls.PlaylistEntries[0].Title,
				PlaylistsNET.Models.M3uPlaylist m3u when m3u.PlaylistEntries.Count > 0
					=> m3u.PlaylistEntries[0].Title,
				_ => null,
			};

			return (paths[0], !string.IsNullOrWhiteSpace(title) ? title : fallbackName);
		}
		catch
		{
			return null;
		}
	}
}
