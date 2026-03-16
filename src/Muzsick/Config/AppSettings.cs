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
	/// Whether to use template-based or AI-generated commentary.
	/// </summary>
	public CommentaryMode CommentaryMode { get; set; } = CommentaryMode.Ai;

	/// <summary>
	/// Default announcement template. Used on first run and by the Reset button.
	/// </summary>
	public const string DefaultAnnouncementTemplate =
		"Now playing {title} by {artist}[year?, released in {year}]";

	/// <summary>
	/// Template used for track announcements. Supports tokens: {title} {artist} {album} {year} {genre}.
	/// </summary>
	public string AnnouncementTemplate { get; set; } = DefaultAnnouncementTemplate;

	/// <summary>
	/// Default AI prompt. Used on first run and by the Reset button.
	/// </summary>
	public const string DefaultAiPrompt =
		"You are an enthusiastic radio DJ. Give a single sentence on-air intro for the next song. " +
		"Track info: {context}. Respond with only the intro sentence, nothing else.";

	/// <summary>
	/// System prompt sent to the AI model. {context} is replaced with track metadata at runtime.
	/// </summary>
	public string AiPrompt { get; set; } = DefaultAiPrompt;

	/// <summary>
	/// Default Ollama base URL.
	/// </summary>
	public const string DefaultOllamaUrl = "http://localhost:11434";

	/// <summary>
	/// Base URL of the Ollama instance.
	/// </summary>
	public string OllamaUrl { get; set; } = DefaultOllamaUrl;

	/// <summary>
	/// Default Ollama model identifier.
	/// </summary>
	public const string DefaultOllamaModel = "gemma3:4b";

	/// <summary>
	/// Ollama model used for AI commentary generation.
	/// </summary>
	public string OllamaModel { get; set; } = DefaultOllamaModel;
}
