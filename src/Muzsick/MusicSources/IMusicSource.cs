// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT

using System;
using Muzsick.Metadata;

namespace Muzsick.MusicSources;

/// <summary>
/// Abstraction over a track detection backend.
/// Implementations raise TrackChanged when a new track is detected.
/// </summary>
public interface IMusicSource : IDisposable
{
	event Action<TrackInfo> TrackChanged;
	void Start();
}
