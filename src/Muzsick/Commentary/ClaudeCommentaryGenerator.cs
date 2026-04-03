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

public class ClaudeCommentaryGenerator(ILogger<ClaudeCommentaryGenerator>? logger = null) : ICommentaryGenerator
{
	private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(45);
	private static readonly Uri _endpoint = new("https://api.anthropic.com/v1/messages");

	private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

	public async Task<string?> GenerateAsync(TrackInfo track, CancellationToken cancellationToken)
	{
		var apiKey = App.Settings.ClaudeApiKey;
		var model = App.Settings.ClaudeModel;
		var prompt = BuildPrompt(track);
		logger?.LogDebug("Claude: sending request for '{Title}' by '{Artist}'", track.Title, track.Artist);

		var requestBody = new JsonObject
		{
			["model"] = model,
			["max_tokens"] = 150,
			["system"] = "Respond in plain text only. No markdown, no headings, no formatting of any kind.",
			["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt, }, },
		};

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(_timeout);

		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
			request.Headers.Add("x-api-key", apiKey);
			request.Headers.Add("anthropic-version", "2023-06-01");
			request.Content = JsonContent.Create(requestBody);

			logger?.LogDebug("Claude: posting to {Endpoint}", _endpoint);
			var response = await _httpClient.SendAsync(request, cts.Token);

			logger?.LogDebug("Claude: HTTP {StatusCode}", response.StatusCode);
			response.EnsureSuccessStatusCode();

			var result = await response.Content.ReadFromJsonAsync<ClaudeMessagesResponse>(cts.Token);
			var raw = result?.Content is { Length: > 0 } blocks ? blocks[0].Text : null;
			logger?.LogDebug("Claude: raw response = {Raw}", raw);

			var content = raw?.Trim();
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
				logger?.LogWarning("Claude: empty response after stripping");
				return null;
			}

			logger?.LogInformation("Claude: commentary = {Content}", content);
			return content;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			logger?.LogWarning("Claude: timed out after {Timeout}s", _timeout.TotalSeconds);
			throw new TimeoutException($"Claude request timed out after {_timeout.TotalSeconds}s.");
		}
		catch (OperationCanceledException)
		{
			logger?.LogDebug("Claude: request cancelled (track change or shutdown)");
			throw;
		}
		catch (Exception ex) when (ex is not HttpRequestException)
		{
			logger?.LogWarning("Claude: unexpected error — {Type}: {Message}", ex.GetType().Name, ex.Message);
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


	private class ClaudeMessagesResponse
	{
		[JsonPropertyName("content")] public ClaudeContentBlock[]? Content { get; init; }
	}

	private class ClaudeContentBlock
	{
		[JsonPropertyName("text")] public string? Text { get; init; }
	}
}
