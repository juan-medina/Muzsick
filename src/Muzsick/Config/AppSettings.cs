// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

namespace Muzsick.Config;

public enum CommentaryMode
{
	Template,
	Ai,
}

public enum AiProvider
{
	Ollama,
	Claude,
}

public class AppSettings
{
	/// <summary>
	/// Playback volume (0–100). Persisted across sessions.
	/// </summary>
	public int Volume { get; set; } = 50;

	/// <summary>
	/// DJ voiceover volume (0–100). Persisted across sessions.
	/// </summary>
	public int DjVolume { get; set; } = 100;


	/// <summary>
	/// Kokoro TTS voice identifier. Defaults to "af_heart".
	/// </summary>
	public string TtsVoice { get; set; } = "af_heart";

	/// <summary>
	/// Whether to use template-based or AI-generated commentary.
	/// </summary>
	public CommentaryMode CommentaryMode { get; set; } = CommentaryMode.Template;

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
	/// Taken from the first entry in the prompt library.
	/// </summary>
	public static string DefaultAiPrompt =>
		Commentary.PromptLibrary.Entries.Count > 0
			? Commentary.PromptLibrary.Entries[0].Prompt
			: "";

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

	/// <summary>
	/// Which AI provider to use when CommentaryMode is Ai.
	/// </summary>
	public AiProvider AiProvider { get; set; } = AiProvider.Ollama;

	/// <summary>
	/// Anthropic API key for Claude commentary generation.
	/// </summary>
	public string ClaudeApiKey { get; set; } = "";

	/// <summary>
	/// Default Claude model identifier.
	/// </summary>
	public const string DefaultClaudeModel = "claude-haiku-4-5";

	/// <summary>
	/// Claude model used for AI commentary generation.
	/// </summary>
	public string ClaudeModel { get; set; } = DefaultClaudeModel;

	/// <summary>
	/// Last directory used in the Browse file picker. Null on first run.
	/// </summary>
	public string? LastBrowseDirectory { get; set; }
}
