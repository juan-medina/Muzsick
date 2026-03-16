// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Muzsick.Metadata;

namespace Muzsick.Commentary;

public class OllamaCommentaryGenerator(ILogger<OllamaCommentaryGenerator>? logger = null) : ICommentaryGenerator
{
	private const string _baseUrl = "http://localhost:11434";
	private const string _model = "gemma3:4b";
	private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(45);

	private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

	public async Task<string?> GenerateAsync(TrackInfo track, CancellationToken cancellationToken)
	{
		var prompt = BuildPrompt(track);
		logger?.LogDebug("Ollama: sending request for '{Title}' by '{Artist}'", track.Title, track.Artist);
		logger?.LogDebug("Ollama: cancellationToken already cancelled = {Val}",
			cancellationToken.IsCancellationRequested);

		var request = new JsonObject
		{
			["model"] = _model,
			["prompt"] = prompt,
			["stream"] = false,
			["think"] = false,
			["options"] = new JsonObject
			{
				["seed"] = Random.Shared.Next(),
			},
		};

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(_timeout);

		try
		{
			logger?.LogDebug("Ollama: posting to {BaseUrl}/api/generate", _baseUrl);
			var response = await _httpClient.PostAsJsonAsync(
				$"{_baseUrl}/api/generate", request, cts.Token);

			logger?.LogDebug("Ollama: HTTP {StatusCode}", response.StatusCode);
			response.EnsureSuccessStatusCode();

			var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cts.Token);
			var content = result?.Response?.Trim();
			if (content != null)
			{
				// Strip markdown formatting
				content = content.Replace("*", "").Replace("_", "").Replace("`", "").Trim();

				var thinkEnd = content.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
				if (thinkEnd >= 0)
					content = content[(thinkEnd + 8)..].Trim();
			}

			if (string.IsNullOrEmpty(content))
			{
				logger?.LogWarning("Ollama: empty response after stripping think block");
				return null;
			}

			logger?.LogInformation("Ollama: commentary = {Content}", content);
			return content;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			logger?.LogWarning("Ollama: timed out after {Timeout}s", _timeout.TotalSeconds);
			throw new TimeoutException($"Ollama request timed out after {_timeout.TotalSeconds}s.");
		}
		catch (OperationCanceledException)
		{
			logger?.LogDebug("Ollama: request cancelled (track change or shutdown)");
			throw;
		}
		catch (Exception ex) when (ex is not HttpRequestException)
		{
			logger?.LogWarning("Ollama: unexpected error — {Type}: {Message}", ex.GetType().Name, ex.Message);
			return null;
		}
	}

	private string BuildPrompt(TrackInfo track)
	{
		var parts = new List<string>();
		if (!string.IsNullOrEmpty(track.Title)) parts.Add($"title: {track.Title}");
		if (!string.IsNullOrEmpty(track.Artist)) parts.Add($"artist: {track.Artist}");
		if (!string.IsNullOrEmpty(track.Album)) parts.Add($"album: {track.Album}");
		if (!string.IsNullOrEmpty(track.Year)) parts.Add($"year: {track.Year}");
		if (!string.IsNullOrEmpty(track.Genre)) parts.Add($"genre: {track.Genre}");

		var context = string.Join(", ", parts);
		return App.Settings.AiPrompt.Replace("{context}", context);
	}


	private class OllamaGenerateResponse
	{
		[JsonPropertyName("response")] public string? Response { get; init; }
	}
}
