// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Muzsick.Commentary;

public class ClaudeCommentaryGenerator(ILogger<ClaudeCommentaryGenerator>? logger = null)
	: AiCommentaryGeneratorBase(logger)
{
	private static readonly Uri _messagesEndpoint = new("https://api.anthropic.com/v1/messages");
	private static readonly Uri _modelsEndpoint = new("https://api.anthropic.com/v1/models");

	protected override string ProviderName => "Claude";

	protected override HttpRequestMessage BuildRequest(string prompt)
	{
		var body = new JsonObject
		{
			["model"] = App.Settings.ClaudeModel,
			["max_tokens"] = 150,
			["system"] = "Respond in plain text only. No markdown, no headings, no formatting of any kind.",
			["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } },
		};

		var message = new HttpRequestMessage(HttpMethod.Post, _messagesEndpoint);
		message.Content = JsonContent.Create(body);
		AddAuthHeaders(message);
		return message;
	}

	protected override string? ParseResponse(string json)
	{
		var node = JsonNode.Parse(json);
		return node?["content"]?.AsArray().FirstOrDefault()?["text"]?.GetValue<string>();
	}

	protected override Uri? ModelsEndpoint => _modelsEndpoint;

	protected override void AddAuthHeaders(HttpRequestMessage request)
	{
		request.Headers.Add("x-api-key", App.Settings.ClaudeApiKey);
		request.Headers.Add("anthropic-version", "2023-06-01");
	}

	protected override IReadOnlyList<string> ParseModels(string json)
	{
		var node = JsonNode.Parse(json);
		return node?["data"]?.AsArray()
			.Select(m => m?["id"]?.GetValue<string>() ?? "")
			.Where(id => id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
			.OrderBy(id => id)
			.ToList() ?? [];
	}
}
