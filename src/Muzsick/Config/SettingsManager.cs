// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muzsick.Config;

public static class SettingsManager
{
	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never,
	};

	public static string SettingsPath { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"Muzsick",
		"settings.json");

	/// <summary>
	/// Loads settings from the app data folder.
	/// Returns null if the file does not exist or cannot be parsed.
	/// </summary>
	public static AppSettings? Load()
	{
		if (!File.Exists(SettingsPath))
			return null;

		try
		{
			var json = File.ReadAllText(SettingsPath);
			return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Saves settings to the app data folder, creating the directory if needed.
	/// </summary>
	public static void Save(AppSettings settings)
	{
		var dir = Path.GetDirectoryName(SettingsPath)!;
		Directory.CreateDirectory(dir);
		var json = JsonSerializer.Serialize(settings, _jsonOptions);
		File.WriteAllText(SettingsPath, json);
	}

	/// <summary>
	/// Creates a settings.json with default empty values if one does not exist yet.
	/// </summary>
	public static void EnsureExists()
	{
		if (!File.Exists(SettingsPath))
			Save(new AppSettings());
	}
}

