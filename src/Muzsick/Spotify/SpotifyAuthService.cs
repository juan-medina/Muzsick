// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

namespace Muzsick.Spotify;

/// <summary>
/// Handles the Spotify PKCE OAuth flow for desktop applications.
/// Opens the system browser, spins up a local callback server on 127.0.0.1:5543,
/// and exchanges the authorization code for a refresh token.
/// </summary>
public class SpotifyAuthService(ILogger? logger = null)
{
	private const int _callbackPort = 5543;
	private static readonly Uri _callbackUri = new($"http://127.0.0.1:{_callbackPort}/callback");

	/// <summary>
	/// Runs the full PKCE authorization flow.
	/// Returns the refresh token on success, or null if the user cancelled,
	/// the flow timed out, or an error occurred.
	/// </summary>
	public async Task<string?> AuthorizeAsync(string clientId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(clientId))
			return null;

		var (verifier, challenge) = PKCEUtil.GenerateCodes();
		var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

		var server = new EmbedIOAuthServer(_callbackUri, _callbackPort);
		string? authCode;

		try
		{
			await server.Start();

			// Lambdas capture only tcs/logger — not server — so there is no
			// captured-variable-disposed-in-outer-scope warning.
			server.AuthorizationCodeReceived += (_, response) =>
			{
				tcs.TrySetResult(response.Code);
				return Task.CompletedTask;
			};

			server.ErrorReceived += (_, error, _) =>
			{
				logger?.LogWarning("[Spotify] OAuth error: {Error}", error);
				tcs.TrySetResult(null);
				return Task.CompletedTask;
			};

			var loginRequest = new LoginRequest(_callbackUri, clientId, LoginRequest.ResponseType.Code)
			{
				CodeChallengeMethod = "S256",
				CodeChallenge = challenge,
				Scope = new[]
				{
					Scopes.UserReadCurrentlyPlaying,
					Scopes.UserReadPlaybackState,
				},
			};

			BrowserUtil.Open(loginRequest.ToUri());
			logger?.LogInformation("[Spotify] Browser opened for authorization");

			// Resolve the TCS if the caller cancels (window close, timeout from caller side)
			cancellationToken.Register(() => tcs.TrySetResult(null));

			authCode = await tcs.Task;
		}
		catch (Exception ex)
		{
			logger?.LogError("[Spotify] Authorization failed: {Message}", ex.Message);
			return null;
		}
		finally
		{
			// Stop and dispose the server once the TCS has resolved (or on exception).
			await server.Stop();
			server.Dispose();
		}

		if (authCode is null)
			return null;

		// Exchange the auth code for tokens outside the server lifetime.
		try
		{
			var tokenResponse = await new OAuthClient().RequestToken(
				new PKCETokenRequest(clientId, authCode, _callbackUri, verifier));
			logger?.LogInformation("[Spotify] OAuth completed — refresh token obtained");
			return tokenResponse.RefreshToken;
		}
		catch (Exception ex)
		{
			logger?.LogError("[Spotify] Token exchange failed: {Message}", ex.Message);
			return null;
		}
	}
}

