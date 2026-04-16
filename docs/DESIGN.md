# Design Document

**Muzsick** — AI-Powered Music Companion
*Version 2.0*

## 1. Project Overview

Muzsick is a lightweight Windows desktop companion that listens to what you play on Spotify and enriches the
experience with AI-generated DJ-style commentary. When a song changes, the application detects the track transition
via the Windows System Media Transport Controls (SMTC), retrieves rich metadata, generates a commentary script,
synthesises a voiceover locally using an on-device neural TTS engine, and plays it through your speakers — all
without interrupting your music.

The application is designed to run fully offline after initial setup. Cloud AI services are supported as optional
enhancements when the user provides API keys, but no internet connection is required for core functionality.

---

## 2. Goals and Non-Goals

### Goals

- Detect song changes from Spotify via Windows SMTC.
- Fetch rich track metadata (artist, album, year, genre, cover art) from free public APIs.
- Generate natural-sounding DJ commentary without requiring external processes or cloud services.
- Play TTS voiceover through the system audio output.
- Provide a clean, minimal desktop UI that stays out of the way.
- Ship as a self-contained Windows installer — no runtime dependencies for the user to manage.

### Non-Goals

- This is not a music player — it does not control or play music itself.
- No support for platforms other than Windows.
- No user account, login, or cloud sync.
- Mobile or web versions are out of scope.

---

## 3. Architecture Overview

The application is built in C# on .NET 9 targeting Windows 10 (19041) and above via Avalonia UI.

```
┌────────────────────────────────────────────────────────────┐
│                      C# Application                        │
│                                                            │
│  ┌──────────────────┐   TrackChanged event                 │
│  │  SmtcWatcher     │────────────────────────┐             │
│  │  (Spotify only)  │                        ▼             │
│  └──────────────────┘             ┌────────────────────┐   │
│                                   │  Metadata Service  │   │
│                                   │  Last.fm API       │   │
│                                   └─────────┬──────────┘   │
│                                             ▼              │
│  ┌──────────────────┐             ┌────────────────────┐   │
│  │  Silk.NET.OpenAL │◄────────────│  Sherpa-ONNX       │   │
│  │  TTS Output      │  PCM buffer │  Kokoro TTS        │   │
│  └────────┬─────────┘             └──────┬─────────────┘   │
│           │                             ▲                  │
│           ▼                    ┌────────┴─────────┐        │
│       Speakers                 │  Commentary Gen  │        │
│                                │  Template / AI   │        │
│  ┌─────────────────────────┐   └──────────────────┘        │
│  │  Avalonia UI            │                               │
│  │  Song · Artist · Art    │                               │
│  └─────────────────────────┘                               │
└────────────────────────────────────────────────────────────┘
```

**Key design principle:** Muzsick does not own the audio pipeline. Spotify plays music through its own pipeline.
Muzsick only plays its TTS voiceover through OpenAL alongside whatever the user is listening to. There is no mixing
or ducking of the Spotify stream — the user controls the balance by setting their Spotify volume and Muzsick's
volume independently.

---

## 4. Technology Stack

| Component           | Technology                          | Notes                              |
|---------------------|-------------------------------------|------------------------------------|
| Language & Runtime  | C# / .NET 9                         | Windows only                       |
| UI Framework        | Avalonia UI                         | Windows 10+                        |
| Track Detection     | Dubya.WindowsMediaController (SMTC) | Spotify sessions only              |
| Audio Output        | Silk.NET.OpenAL                     | TTS playback only                  |
| Text-to-Speech      | Sherpa-ONNX (C# bindings)           | In-process, no subprocess          |
| TTS Voice Model     | Kokoro-82M (ONNX export)            | Downloaded automatically at build  |
| Track Metadata      | Last.fm API (`track.getInfo`)       | HTTP via HttpClient                |
| Artist Images       | Wikimedia Commons / Wikidata        | HTTP via HttpClient                |
| AI Commentary       | Ollama or Anthropic Claude API      | Optional, user-configured          |
| Configuration       | System.Text.Json — settings.json    | No registry entries                |

---

## 5. Component Details

### 5.1 Track Detection — SmtcWatcher

`SmtcWatcher` uses `Dubya.WindowsMediaController` to listen to Windows System Media Transport Controls sessions.
It filters to Spotify sessions only (app ID contains "Spotify"). When a track change event fires with a non-empty
title and artist, it raises a `TrackChanged` event with a `TrackInfo` object.

Empty metadata events (which Spotify fires on session open before a track is known) are silently ignored.

Future versions may extend this to support other sources (browser-based radio, YouTube) via a configurable allow
list.

### 5.2 Audio Output — Silk.NET.OpenAL

OpenAL manages a single TTS audio source. When a voiceover is ready, the PCM buffer is loaded into the source and
played. There is no radio source and no ducking — the user manages the balance between Spotify and Muzsick's
voiceover using their system volume controls.

### 5.3 Text-to-Speech — Sherpa-ONNX + Kokoro

Sherpa-ONNX provides a C# NuGet package that wraps an ONNX runtime, enabling fully in-process neural TTS inference.
The Kokoro-82M model produces natural-sounding speech well suited to DJ-style announcements and runs in real time on
a modest CPU.

The model files are not committed to the repository. MSBuild downloads and extracts them automatically on the first
`dotnet build`.

### 5.4 Track Metadata — Last.fm + Wikidata

`LastFmMetaService` resolves track information to rich metadata via `track.getInfo`. It also retrieves a
MusicBrainz ID (MBID) from the Last.fm response, which is passed to `WikidataArtistService` to resolve an artist
image URL via Wikidata and Wikimedia Commons.

Because Spotify SMTC provides clean, structured artist and title fields, the multi-stage ICY fallback strategy is
not needed. A single `track.getInfo` call with the exact artist and title is sufficient.

### 5.5 Commentary Generation

Commentary is generated in one of two modes controlled by the `CommentaryMode` setting.

**Template mode** generates commentary from a user-editable template with token substitution. Supported tokens:
`{title}`, `{artist}`, `{album}`, `{year}`, `{genre}`. Optional clauses (`[token?, text]`) are silently dropped
when the guarded token is empty.

The default template is:

```
Now playing {title} by {artist}[year?, released in {year}]
```

**AI mode** sends a user-editable prompt to either a locally running Ollama instance or the Anthropic Claude API,
controlled by the `AiProvider` setting. The prompt receives enriched track metadata as context. If the AI request
fails or times out, commentary automatically falls back to the template.

### 5.6 Commentary Timing

Commentary fires approximately 3 seconds after a track change event, to allow metadata fetch to complete. If a new
track arrives before the previous voiceover completes, the previous work is cancelled. While the settings window is
open, live commentary is fully suspended.

---

## 6. User Interface

| Element               | Description                                                                                                   |
|-----------------------|---------------------------------------------------------------------------------------------------------------|
| Artist image          | Circular image in the header. Placeholder while loading. Clickable — opens the artist's Last.fm page.        |
| Album art             | Square cover art in the main area. Placeholder while loading. Clickable — opens the Last.fm album page.      |
| Track title           | Large, bold. Clickable — opens the Last.fm track page.                                                       |
| Artist name           | Secondary text. Clickable — opens the Last.fm artist page.                                                   |
| Album name            | Tertiary text. Shows album and year when available. Clickable — opens the Last.fm album page.                 |
| Volume slider         | Controls the OpenAL TTS output gain (0–100%). Persisted across sessions.                                     |
| History button        | Opens the session history window showing the last 20 tracks with per-track commentary replay.                 |
| Settings button       | Opens the settings window. Suspends live commentary while open.                                              |
| About button          | Opens the about window with version info and attribution.                                                    |
| Update banner         | Appears when a new version has been staged by Velopack.                                                      |

---

## 7. Configuration

User preferences are stored in `settings.json` in the application data folder. Read on startup, written on any
change. No registry entries.

```json
{
  "Volume": 50,
  "DjVolume": 100,
  "TtsVoice": "af_heart",
  "CommentaryMode": "Template",
  "AnnouncementTemplate": "Now playing {title} by {artist}[year?, released in {year}]",
  "AiPrompt": "You are an enthusiastic radio DJ. Give a single sentence on-air intro for the next song. Track info: {context}. Respond with only the intro sentence, nothing else.",
  "OllamaUrl": "http://localhost:11434",
  "OllamaModel": "gemma3:4b",
  "AiProvider": "Ollama",
  "ClaudeApiKey": "",
  "ClaudeModel": "claude-haiku-4-5"
}
```

---

## 8. Key Design Decisions

### 8.1 Why SMTC Instead of Spotify Web API

The Spotify Web API requires OAuth, a registered app with a Client ID, and a Premium subscription. As of February
2026, development mode apps are limited to five authorized users, making distribution to an open source audience
impossible without each user registering their own app. SMTC requires no credentials and works with any Spotify
account tier.

### 8.2 Spotify Only (for Now)

SMTC exposes sessions from all media players, but browser sessions (Edge, Chrome) aggregate all tabs into one
session — a Twitch stream and a YouTube video are indistinguishable at the OS level. Spotify has its own dedicated
session with clean, structured metadata. Future versions may add a configurable source filter (by app ID or artist
name) to support additional sources.

### 8.3 No Audio Ducking

In the previous radio architecture, Muzsick owned the audio pipeline and could duck the radio source by adjusting
OpenAL gain. With Spotify playing through its own pipeline, there is no clean programmatic handle on Spotify's audio
session (`ISimpleAudioVolume` cannot cross process boundaries). The endpoint volume (`IAudioEndpointVolume`) could
duck everything on the device, but that is too blunt. The user-controlled balance approach is simpler and gives the
user direct control.

### 8.4 Windows Only

The macOS `MediaRemote` private framework was locked down in macOS 15.4 — only entitled Apple processes can access
now-playing information. Linux MPRIS via D-Bus would work but adds significant platform-specific complexity. Given
that Spotify's primary desktop platform is Windows, Windows-only is the right scope for now.

### 8.5 Why Silk.NET.OpenAL for TTS Output

OpenAL is retained for TTS playback because Sherpa-ONNX returns raw PCM and OpenAL handles it cleanly without
additional dependencies. The radio-streaming use of OpenAL (PCM buffer queuing, buffer recycling, gain fading) has
been removed entirely.

### 8.6 Two AI Commentary Backends

- **`OllamaCommentaryGenerator`** — local, uses Ollama's `/api/generate` endpoint. Keeps all data on-device.
- **`ClaudeCommentaryGenerator`** — cloud, uses the Anthropic Messages API. Requires an API key.

Both backends build the prompt identically and honour the same 45-second timeout. Switching providers requires only
a settings change.

---

## 9. Version Roadmap

| Version | Scope              | Key Deliverables                                                              |
|---------|--------------------|-------------------------------------------------------------------------------|
| 2.0     | SMTC Foundation    | Spotify track detection, TTS commentary, metadata, settings UI, Windows only. |
| TBD     | Source Filters     | Configurable allow/block list by app ID and artist for browser sources.       |
| TBD     | Prompt Library     | Built-in personality presets. Save/load custom prompts.                       |
| TBD     | Conversation       | User can ask questions about the current track via text input.                |
