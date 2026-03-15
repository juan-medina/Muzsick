// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

namespace Muzsick.Config;

public enum CommentaryMode
{
	Template,
	Ai,
}

public class AppSettings
{
	/// <summary>
	/// Last.fm API key. Required for track metadata enrichment.
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
	/// </summary>
	public string AnnouncementTemplate { get; set; } = AnnouncementTemplateRenderer.DefaultTemplate;

	/// <summary>
	/// Whether to use template-based or AI-generated commentary.
	/// </summary>
	public CommentaryMode CommentaryMode { get; set; } = CommentaryMode.Ai;
}
