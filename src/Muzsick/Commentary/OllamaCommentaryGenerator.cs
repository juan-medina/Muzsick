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

public class OllamaCommentaryGenerator(ILogger<OllamaCommentaryGenerator>? logger = null)
	: AiCommentaryGeneratorBase(logger)
{
	protected override string ProviderName => "Ollama";

	protected override HttpRequestMessage BuildRequest(string prompt)
	{
		var body = new JsonObject
		{
			["model"] = App.Settings.OllamaModel,
			["prompt"] = prompt,
			["stream"] = false,
			["think"] = false,
			["options"] = new JsonObject
			{
				["seed"] = Random.Shared.Next(),
			},
		};

		var message = new HttpRequestMessage(HttpMethod.Post,
			$"{App.Settings.OllamaUrl}/api/generate");
		message.Content = JsonContent.Create(body);
		return message;
	}

	protected override string? ParseResponse(string json)
	{
		var node = JsonNode.Parse(json);
		return node?["response"]?.GetValue<string>();
	}

	protected override Uri? ModelsEndpoint
	{
		get
		{
			var url = App.Settings.OllamaUrl.TrimEnd('/');
			return Uri.TryCreate($"{url}/api/tags", UriKind.Absolute, out var uri) ? uri : null;
		}
	}

	protected override IReadOnlyList<string> ParseModels(string json)
	{
		var node = JsonNode.Parse(json);
		return node?["models"]?.AsArray()
			.Select(m => m?["name"]?.GetValue<string>() ?? "")
			.Where(name => !string.IsNullOrEmpty(name))
			.ToList() ?? [];
	}
}
