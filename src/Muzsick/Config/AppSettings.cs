// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

namespace Muzsick.Config;

public class AppSettings
{
	/// <summary>
	/// Last.fm API key. Required for track metadata enrichment.
	/// Obtain a free key at https://www.last.fm/api/account/create
	/// </summary>
	public string LastFmApiKey { get; set; } = "";

	/// <summary>
	/// Playback volume (0–100). Persisted across sessions.
	/// </summary>
	public int Volume { get; set; } = 50;

	/// <summary>
	/// Absolute path of the last playlist file opened. Restored on next launch.
	/// </summary>
	public string? LastPlaylistPath { get; set; }

	/// <summary>
	/// Kokoro TTS voice identifier. Defaults to "af_heart".
	/// </summary>
	public string TtsVoice { get; set; } = "af_heart";

	/// <summary>
	/// Template used for track announcements. Supports tokens: {title} {artist} {album} {year} {genre}.
	/// Wrap optional parts in [...] to silently drop them when any token inside is empty.
	/// </summary>
	public string AnnouncementTemplate { get; set; } = AnnouncementTemplateRenderer.DefaultTemplate;
}
