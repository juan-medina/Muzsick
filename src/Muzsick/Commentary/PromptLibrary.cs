// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muzsick.Commentary;

public record PromptLibraryEntry(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("description")] string Description,
	[property: JsonPropertyName("prompt")] string Prompt);

public static class PromptLibrary
{
	private static readonly Lazy<IReadOnlyList<PromptLibraryEntry>> _entries = new(Load);

	public static IReadOnlyList<PromptLibraryEntry> Entries => _entries.Value;

	private static IReadOnlyList<PromptLibraryEntry> Load()
	{
		var assembly = Assembly.GetExecutingAssembly();
		const string resourceName = "Muzsick.prompt-library.json";

		using var stream = assembly.GetManifestResourceStream(resourceName);
		if (stream == null)
			return [];

		var entries = JsonSerializer.Deserialize<List<PromptLibraryEntry>>(stream);
		return entries?.AsReadOnly() ?? (IReadOnlyList<PromptLibraryEntry>)[];
	}
}
