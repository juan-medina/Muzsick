// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DiscordRPC;
using Microsoft.Extensions.Logging;
using Muzsick.Metadata;

namespace Muzsick.Discord;

public sealed class DiscordPresenceService : IDisposable
{
	private static readonly string _appId = LoadAppId();
	private readonly ILogger? _logger;
	private readonly DiscordRpcClient _client;

	public DiscordPresenceService(ILogger? logger = null)
	{
		_logger = logger;
		_client = new DiscordRpcClient(_appId);
		_client.Initialize();
		_logger?.LogInformation("Discord: presence service initialised");
	}

	public void UpdateTrack(TrackInfo track)
	{
		if (string.IsNullOrEmpty(_appId))
		{
			_logger?.LogWarning("Discord: app ID not configured, skipping presence update");
			return;
		}

		try
		{
			_client.SetPresence(new RichPresence
			{
				Details = track.Title,
				State = track.Artist,
				Assets = new Assets { LargeImageKey = track.CoverArtUrl ?? "muzsick", LargeImageText = track.Album, },
				Timestamps = Timestamps.Now,
			});

			_logger?.LogDebug("Discord: updated presence for '{Title}' by '{Artist}'", track.Title, track.Artist);
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Discord: failed to update presence");
		}
	}

	public void Clear()
	{
		try
		{
			_client.ClearPresence();
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Discord: failed to clear presence");
		}
	}

	public void Dispose() => _client.Dispose();

	private static string LoadAppId()
	{
		try
		{
			using var stream = Assembly.GetExecutingAssembly()
				.GetManifestResourceStream("Muzsick.ApiKeys.json");
			if (stream == null) return "";
			using var reader = new StreamReader(stream);
			using var doc = JsonDocument.Parse(reader.ReadToEnd());
			return doc.RootElement.GetProperty("DiscordAppId").GetString() ?? "";
		}
		catch
		{
			return "";
		}
	}
}
