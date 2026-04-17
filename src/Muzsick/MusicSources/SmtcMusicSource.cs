// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using Microsoft.Extensions.Logging;
using Muzsick.Metadata;
using WindowsMediaController;

namespace Muzsick.MusicSources;

/// <summary>
/// Detects Spotify track changes via Windows System Media Transport Controls.
/// Windows only. Requires no credentials.
/// </summary>
public sealed class SmtcMusicSource : IMusicSource
{
	private readonly ILogger? _logger;
	private readonly MediaManager _mediaManager = new();

	public event Action<TrackInfo>? TrackChanged;

	public SmtcMusicSource(ILogger? logger = null)
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

		// Spotify fires an empty metadata event on session open before the track is known
		if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return;

		_logger?.LogInformation(
			"[SMTC] Track changed | Title={Title} | Artist={Artist} | Album={Album}",
			title, artist, props.AlbumTitle);

		TrackChanged?.Invoke(new TrackInfo
		{
			Title = title,
			Artist = artist,
			Album = props.AlbumTitle ?? string.Empty,
		});
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

