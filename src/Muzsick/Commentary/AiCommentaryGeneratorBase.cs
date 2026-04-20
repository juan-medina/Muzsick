// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Muzsick.Metadata;

namespace Muzsick.Commentary;

public abstract class AiCommentaryGeneratorBase : ICommentaryGenerator
{
	private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(45);

	private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

	protected abstract string ProviderName { get; }

	protected abstract HttpRequestMessage BuildRequest(string prompt);

	protected abstract string? ParseResponse(string json);

	protected virtual Uri? ModelsEndpoint => null;

	protected virtual void AddAuthHeaders(HttpRequestMessage request) { }

	protected virtual IReadOnlyList<string> ParseModels(string json) => [];

	protected readonly ILogger? Logger;

	protected AiCommentaryGeneratorBase(ILogger? logger)
	{
		Logger = logger;
	}

	public async Task<CommentaryResult> GenerateAsync(TrackInfo track, CancellationToken cancellationToken)
	{
		var prompt = BuildPrompt(track);
		Logger?.LogDebug("{Provider}: sending request for '{Title}' by '{Artist}'",
			ProviderName, track.Title, track.Artist);

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(_timeout);

		try
		{
			using var request = BuildRequest(prompt);
			Logger?.LogDebug("{Provider}: sending request to {Url}", ProviderName, request.RequestUri);
			var response = await _httpClient.SendAsync(request, cts.Token);

			Logger?.LogDebug("{Provider}: HTTP {StatusCode}", ProviderName, response.StatusCode);

			var json = await response.Content.ReadAsStringAsync(cts.Token);

			if (!response.IsSuccessStatusCode)
			{
				var error = MapErrorStatus((int)response.StatusCode, json);
				return CommentaryResult.Fail(error);
			}
			Logger?.LogDebug("{Provider}: raw response = {Raw}", ProviderName, json);

			var content = ParseResponse(json)?.Trim();
			if (content != null)
				content = StripMarkdown(content);

			if (string.IsNullOrEmpty(content))
			{
				Logger?.LogWarning("{Provider}: empty response after stripping", ProviderName);
				return CommentaryResult.Fail(CommentaryError.EmptyResponse);
			}

			Logger?.LogInformation("{Provider}: commentary = {Content}", ProviderName, content);
			return CommentaryResult.Success(content);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			Logger?.LogWarning("{Provider}: timed out after {Timeout}s", ProviderName, _timeout.TotalSeconds);
			return CommentaryResult.Fail(CommentaryError.Timeout);
		}
		catch (OperationCanceledException)
		{
			Logger?.LogDebug("{Provider}: request cancelled (track change or shutdown)", ProviderName);
			return CommentaryResult.Fail(CommentaryError.Cancelled);
		}
		catch (HttpRequestException ex)
		{
			Logger?.LogWarning("{Provider}: network error — {Message}", ProviderName, ex.Message);
			return CommentaryResult.Fail(CommentaryError.Unreachable);
		}
		catch (Exception ex)
		{
			Logger?.LogWarning("{Provider}: unexpected error — {Type}: {Message}",
				ProviderName, ex.GetType().Name, ex.Message);
			return CommentaryResult.Fail(CommentaryError.ServerError);
		}
	}

	public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken)
	{
		var endpoint = ModelsEndpoint;
		if (endpoint == null)
			return [];

		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
			AddAuthHeaders(request);
			var response = await _httpClient.SendAsync(request, cancellationToken);
			if (!response.IsSuccessStatusCode)
				return [];

			var json = await response.Content.ReadAsStringAsync(cancellationToken);
			return ParseModels(json);
		}
		catch (Exception ex)
		{
			Logger?.LogWarning("{Provider}: failed to fetch models — {Type}: {Message}",
				ProviderName, ex.GetType().Name, ex.Message);
			return [];
		}
	}

	protected virtual CommentaryError MapErrorStatus(int statusCode, string? responseBody) =>
		statusCode switch
		{
			401 => CommentaryError.Unauthorized,
			429 => CommentaryError.RateLimited,
			402 or 529 => CommentaryError.QuotaExceeded,
			_ => CommentaryError.ServerError,
		};

	protected static string BuildPrompt(TrackInfo track)
	{
		var parts = new System.Collections.Generic.List<string>();
		if (!string.IsNullOrEmpty(track.Title)) parts.Add($"title: {track.Title}");
		if (!string.IsNullOrEmpty(track.Artist)) parts.Add($"artist: {track.Artist}");
		if (!string.IsNullOrEmpty(track.Album)) parts.Add($"album: {track.Album}");
		if (!string.IsNullOrEmpty(track.Year)) parts.Add($"year: {track.Year}");
		if (!string.IsNullOrEmpty(track.Genre)) parts.Add($"genre: {track.Genre}");

		var context = string.Join(", ", parts);
		return App.Settings.AiPrompt.Replace("{context}", context);
	}

	private static string StripMarkdown(string content)
	{
		content = content.Replace("*", "").Replace("_", "").Replace("`", "").Trim();

		var thinkEnd = content.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
		if (thinkEnd >= 0)
			content = content[(thinkEnd + 8)..].Trim();

		return content;
	}
}

