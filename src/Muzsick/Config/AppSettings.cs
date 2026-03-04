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
}

