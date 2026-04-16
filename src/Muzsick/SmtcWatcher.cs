// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using Microsoft.Extensions.Logging;
using Muzsick.Metadata;
using WindowsMediaController;

namespace Muzsick;

public sealed class SmtcWatcher : IDisposable
{
	private readonly ILogger? _logger;
	private readonly MediaManager _mediaManager = new();

	// Fires when a Spotify track changes. Title and Artist are always non-empty.
	public event Action<TrackInfo>? TrackChanged;

	public SmtcWatcher(ILogger? logger = null)
	{
		_logger = logger;
	}

	public void Start()
	{
		_mediaManager.OnAnyMediaPropertyChanged += OnMediaPropertyChanged;
		_mediaManager.OnAnyPlaybackStateChanged += OnPlaybackStateChanged;
		_mediaManager.OnAnySessionOpened += OnSessionOpened;
		_mediaManager.Start();
		_logger?.LogInformation("[SMTC] Watcher started");
	}

	private void OnSessionOpened(MediaManager.MediaSession session)
	{
		if (!IsSpotify(session)) return;
		_logger?.LogInformation("[SMTC] Spotify session opened | App={App}", session.Id);
	}

	private void OnMediaPropertyChanged(
		MediaManager.MediaSession session,
		Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties props)
	{
		if (!IsSpotify(session)) return;

		var title = props.Title;
		var artist = props.Artist;

		// Skip empty metadata events — Spotify fires one on session open before the track is known
		if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return;

		_logger?.LogInformation(
			"[SMTC] Track changed | Title={Title} | Artist={Artist} | Album={Album}",
			title, artist, props.AlbumTitle);

		var track = new TrackInfo
		{
			Title = title,
			Artist = artist,
			Album = props.AlbumTitle ?? string.Empty,
		};

		TrackChanged?.Invoke(track);
	}

	private void OnPlaybackStateChanged(
		MediaManager.MediaSession session,
		Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackInfo info)
	{
		if (!IsSpotify(session)) return;
		_logger?.LogDebug("[SMTC] Playback changed | Status={Status}", info.PlaybackStatus);
	}

	private static bool IsSpotify(MediaManager.MediaSession session) =>
		session.Id.Contains("Spotify", StringComparison.OrdinalIgnoreCase);

	public void Dispose()
	{
		_mediaManager.OnAnyMediaPropertyChanged -= OnMediaPropertyChanged;
		_mediaManager.OnAnyPlaybackStateChanged -= OnPlaybackStateChanged;
		_mediaManager.OnAnySessionOpened -= OnSessionOpened;
		_mediaManager.Dispose();
	}
}
