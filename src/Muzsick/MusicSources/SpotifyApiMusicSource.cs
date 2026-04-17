// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Muzsick.Config;
using Muzsick.Metadata;
using SpotifyAPI.Web;

namespace Muzsick.MusicSources;

/// <summary>
/// Detects track changes by polling the Spotify Web API every 5 seconds.
/// Handles token refresh automatically via PKCEAuthenticator.
/// Available on all platforms. Requires Spotify Premium and OAuth credentials.
/// </summary>
public sealed class SpotifyApiMusicSource : IMusicSource
{
	private readonly ILogger? _logger;
	private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
	private CancellationTokenSource _cts = new();
	private string? _lastTrackId;

	public event Action<TrackInfo>? TrackChanged;

	public SpotifyApiMusicSource(ILogger? logger = null)
	{
		_logger = logger;
	}

	public void Start()
	{
		_cts = new CancellationTokenSource();
		_ = PollLoopAsync(_cts.Token);
		_logger?.LogInformation("[SpotifyAPI] Polling started");
	}

	private async Task PollLoopAsync(CancellationToken cancellationToken)
	{
		SpotifyClient? client = null;
		string? builtWithClientId = null;

		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				// Rebuild client only if we don't have one or the client ID changed
				// (e.g. user reconnected with different credentials).
				// Token rotation is handled internally by PKCEAuthenticator — do not
				// compare refresh tokens here, as they change on every refresh.
				var currentClientId = App.Settings.SpotifyClientId;
				if (client is null || currentClientId != builtWithClientId)
				{
					builtWithClientId = currentClientId;
					client = await BuildClientAsync(App.Settings.SpotifyRefreshToken, cancellationToken);
				}

				if (client is not null)
					await PollOnceAsync(client, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger?.LogWarning("[SpotifyAPI] Poll error: {Message}", ex.Message);
				client = null;
			}

			await Task.Delay(_pollInterval, cancellationToken);
		}

		_logger?.LogInformation("[SpotifyAPI] Polling stopped");
	}

	private async Task<SpotifyClient?> BuildClientAsync(
		string? refreshToken,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(App.Settings.SpotifyClientId))
		{
			_logger?.LogWarning("[SpotifyAPI] Missing credentials — cannot build client");
			return null;
		}

		try
		{
			var response = await new OAuthClient().RequestToken(
				new PKCETokenRefreshRequest(App.Settings.SpotifyClientId, refreshToken),
				cancellationToken);

			// PKCE refresh tokens are single-use — persist the new one immediately
			App.Settings.SpotifyRefreshToken = response.RefreshToken;
			SettingsManager.Save(App.Settings);

			var authenticator = new PKCEAuthenticator(App.Settings.SpotifyClientId, response);
			authenticator.TokenRefreshed += (_, newResponse) =>
			{
				App.Settings.SpotifyRefreshToken = newResponse.RefreshToken;
				SettingsManager.Save(App.Settings);
				_logger?.LogDebug("[SpotifyAPI] Token refreshed and persisted");
			};

			var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
			_logger?.LogInformation("[SpotifyAPI] Client built successfully");
			return new SpotifyClient(config);
		}
		catch (Exception ex)
		{
			_logger?.LogError("[SpotifyAPI] Failed to build client: {Message}", ex.Message);
			return null;
		}
	}

	private async Task PollOnceAsync(SpotifyClient client, CancellationToken cancellationToken)
	{
		var current = await client.Player.GetCurrentlyPlaying(
			new PlayerCurrentlyPlayingRequest(), cancellationToken);

		if (current.Item is not FullTrack track)
			return;

		// Only fire if the track ID has changed
		if (track.Id == _lastTrackId)
			return;

		_lastTrackId = track.Id;

		var artist = track.Artists.Count > 0 ? track.Artists[0].Name : "";

		if (string.IsNullOrWhiteSpace(track.Name) || string.IsNullOrWhiteSpace(artist))
			return;

		_logger?.LogInformation(
			"[SpotifyAPI] Track changed | Title={Title} | Artist={Artist} | Album={Album}",
			track.Name, artist, track.Album.Name);

		TrackChanged?.Invoke(new TrackInfo
		{
			Title = track.Name,
			Artist = artist,
			Album = track.Album.Name,
		});
	}

	public void Dispose()
	{
		_cts.Cancel();
		_cts.Dispose();
	}
}
