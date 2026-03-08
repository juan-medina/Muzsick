# Design Document

**Muzsick** — AI-Powered Radio Companion
*Version 1.0*

## 1. Project Overview

Muzsick is a lightweight desktop radio companion that plays live internet radio streams and enriches the listening
experience with AI-generated spoken commentary. When a song changes, the application detects the track transition,
retrieves rich metadata, generates a DJ-style commentary script, synthesises the voiceover locally using an on-device
neural text-to-speech engine, and seamlessly mixes it into the ongoing audio stream — all without interrupting
playback.

The application is designed to run fully offline after initial setup. Cloud AI services are supported as optional
enhancements when the user chooses to provide API keys, but no internet connection is required for core functionality.

---

## 2. Goals and Non-Goals

### Goals

- Play live `.pls` and `.m3u` audio streams reliably with low latency.
- Detect song changes via ICY metadata from the stream.
- Fetch rich track metadata (artist, album, year, genre, cover art) from free public APIs.
- Generate natural-sounding DJ commentary without requiring external processes or cloud services.
- Mix the TTS voiceover into the radio stream with smooth volume ducking and fade-back.
- Provide a clean, minimal desktop UI that stays out of the way.
- Ship as a self-contained installer — no runtime dependencies for the user to manage.
- Run cross-platform on Windows, macOS, and Linux.

### Non-Goals

- This is not a full music player or podcast manager.
- No user account, login, or cloud sync.
- No support for DRM-protected streams.
- Mobile or web versions are out of scope.

---

## 3. Architecture Overview

The application is built in C# on .NET 9 targeting Windows, macOS, and Linux via Avalonia UI. All audio processing —
stream playback, TTS output, and mixing — is handled in-process through Silk.NET.OpenAL. There are no subprocesses
at runtime.

```
┌────────────────────────────────────────────────────────────┐
│                      C# Application                        │
│                                                            │
│  ┌──────────────────┐   ICY metadata event                 │
│  │  LibVLCSharp     │────────────────────────┐             │
│  │  Stream Player   │                        ▼             │
│  └────────┬─────────┘             ┌────────────────────┐   │
│           │ PCM audio             │  Metadata Service  │   │
│           ▼                       │  Last.fm API       │   │
│  ┌──────────────────┐             └─────────┬──────────┘   │
│  │  Silk.NET.OpenAL │◄────────────┐         ▼              │
│  │  Mixer + Output  │  PCM buffer │  ┌────────────────┐    │
│  │  + Volume Duck   │◄────────────┘  │  Sherpa-ONNX   │    │
│  └────────┬─────────┘                │  Kokoro TTS    │    │
│           │                          └──────┬─────────┘    │
│           ▼                                 ▲              │
│       Speakers                     ┌────────┴─────────┐    │
│                                    │  Commentary Gen  │    │
│  ┌─────────────────────────────┐   │  Template / AI   │    │
│  │  Avalonia UI                │   └──────────────────┘    │
│  │  Song · Artist · Album Art  │                           │
│  └─────────────────────────────┘                           │
└────────────────────────────────────────────────────────────┘
```

**Key design principle:** Silk.NET.OpenAL is the single audio output point. LibVLCSharp decodes the stream and feeds
raw PCM data into OpenAL as one audio source. When a voiceover is ready, it is loaded as a second OpenAL source into
the same context. Ducking is achieved by adjusting the gain on the stream source — LibVLCSharp never touches the
output device directly.

---

## 4. Technology Stack

| Component           | Technology                       | Notes                              |
|---------------------|----------------------------------|------------------------------------|
| Language & Runtime  | C# / .NET 9                      | Cross-platform                     |
| UI Framework        | Avalonia UI                      | Windows, macOS, Linux              |
| Stream Playback     | LibVLCSharp + VideoLAN.LibVLC    | NuGet + native libs                |
| Audio Mixing/Output | Silk.NET.OpenAL                  | Cross-platform, replaces NAudio    |
| Text-to-Speech      | Sherpa-ONNX (C# bindings)        | In-process, no subprocess          |
| TTS Voice Model     | Kokoro-82M (ONNX export)         | Downloaded automatically at build  |
| Track Metadata      | Last.fm API (`track.getInfo`)    | HTTP via HttpClient                |
| Artist Images       | Wikimedia Commons / Wikidata     | HTTP via HttpClient                |
| AI Commentary       | OpenAI-compatible HTTP API       | Optional, user-configured endpoint |
| Configuration       | System.Text.Json — settings.json | No registry entries                |

---

## 5. Component Details

### 5.1 Stream Playback — LibVLCSharp

LibVLCSharp provides the official .NET bindings for libvlc. It supports `.pls` and `.m3u` playlist formats, handles
reconnection on stream drop, and surfaces ICY metadata change events that signal a song transition.

LibVLCSharp is configured to output decoded PCM audio via a callback into an OpenAL streaming buffer, rather than
directly to a sound device. This gives OpenAL full control over the final output mix.

### 5.2 Audio Mixing and Ducking — Silk.NET.OpenAL

OpenAL manages two audio sources: the continuous radio stream and an on-demand TTS source. Each source has its own
gain value that can be adjusted independently at any time.

When a voiceover is ready, the following sequence executes:

1. Fade radio source gain down to 20% over 500ms.
2. Begin playing the TTS PCM buffer through the TTS source.
3. When TTS playback completes, fade radio source gain back to 100% over 800ms.

Both sources are mixed by OpenAL natively — no separate routing layer or inter-process audio is needed.

### 5.3 Text-to-Speech — Sherpa-ONNX + Kokoro

Sherpa-ONNX provides a C# NuGet package that wraps an ONNX runtime, enabling fully in-process neural TTS inference.
The Kokoro-82M model produces natural-sounding speech well suited to DJ-style announcements and runs in real time on
a modest CPU.

The model files are not committed to the repository. MSBuild downloads and extracts them automatically on the first
`dotnet build`. Subsequent builds skip the download. The `models/` folder at the repo root is gitignored.

Once downloaded, no internet connection is required for TTS synthesis.

### 5.4 Metadata — Last.fm + Wikidata

When a song change is detected, the artist name and track title from the ICY metadata are used to look up enrichment
data from two sources.

**Track metadata — Last.fm `track.getInfo`**

The Last.fm `track.getInfo` endpoint is designed exactly for `artist + title` lookups from playback events. It
returns album name, release year, genre tags, and album art URLs in a single call. A free API key is required.

**Artist images — WikidataArtistService**

Last.fm does not provide artist portrait images, but its `artist.getInfo` response includes the artist's MusicBrainz
ID (MBID). `WikidataArtistService` uses that MBID to resolve an image via three external calls: MusicBrainz url-rels
(MBID → Wikidata URL), Wikidata API (QID → image filename), Wikimedia Commons (filename → thumbnail URL).

This service is self-contained — it takes an MBID string and returns an image URL, with no dependency on which
metadata service called it. The `MetaBrainz.MusicBrainz` NuGet package lives here for the url-rels step.

**Track lookup strategy**

ICY metadata is unreliable — artist names are often comma-joined for multi-credit tracks, titles may include
featured-artist decorators or version labels, and some stations censor or alter titles entirely. `LastFmMetaService`
uses a multi-stage strategy:

```
ICY string: "Wilkinson, ILIRA, iiola, Tom Cane" — "Infinity (feat. ILIRA, iiola & Tom Cane)"

Stage 1 — Exact ICY artist + exact ICY title
  → returns track with no album/art (comma-joined name matches obscure entry)
  → result is not rich, continue

Stage 2 — Primary artist + exact ICY title
  → split artist on first comma → "Wilkinson"
  → full album + cover art returned  ✓

─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─

ICY string: "Roddy Ricch" — "The Box"

Stage 1 — autocorrect=1 drifts to compilation "Just Hits" (Various Artists)
  → IsCompilation = true, retry with autocorrect=0
Stage 1b — returns original album "Please Excuse Me for Being Antisocial"  ✓

─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─

ICY string: "Zolita" — "Somebody I Did Once"   (station censored the real title)

Stages 1–4 → not found
Stage 5 — track.search fallback
  → top result: "Somebody I F*cked Once" by Zolita
  → getInfo with corrected title → album + cover art returned  ✓
```

Each stage only runs if the previous returned null or a non-rich result (no album, no cover art). All results are
cached keyed on `artist+title` and expire after 24 hours.

### 5.5 Commentary Generation

Commentary is generated from a user-editable template with token substitution. Supported tokens: `{title}`,
`{artist}`, `{album}`, `{year}`, `{genre}`. Optional clauses (`[token?, text]`) are silently dropped when the
guarded token is empty, keeping announcements natural when enrichment data is partial.

The default template is:

```
Now playing {title} by {artist}[year?, released in {year}]
```

The template is editable in the settings window with live syntax highlighting and a speak-preview button that
synthesises the rendered result through the TTS engine before saving.

### 5.6 Commentary Timing

Commentary fires at the start of a new song. The application waits approximately 3 seconds after the ICY event
before beginning metadata fetch and TTS synthesis, to avoid interrupting a song that is mid-fade or mid-intro. If a
new track arrives before the previous voiceover completes, the previous work is cancelled.

---

## 6. User Interface

The Avalonia UI is intentionally minimal — the audio experience is the product; the UI is a control surface.

| Element             | Description                                                                 |
|---------------------|-----------------------------------------------------------------------------|
| Album art           | Loaded from Last.fm. XAML vinyl-record placeholder shown while loading.     |
| Artist image        | Circular image in the header. XAML silhouette placeholder while loading.    |
| Track title         | Large, bold. Truncated with ellipsis if too long.                           |
| Artist name         | Secondary text below the title.                                             |
| Play / Pause        | Toggles stream playback.                                                    |
| Volume slider       | Controls OpenAL master output gain (0–100%). Persisted across sessions.     |
| Settings button     | Opens the settings window: API key, TTS voice, announcement template.       |
| About button        | Opens the about window with version info and attribution.                   |

---

## 7. Configuration

User preferences are stored in `settings.json` in the application data folder. Read on startup, written on any
change. No registry entries.

```json
{
  "LastFmApiKey": "",
  "Volume": 50,
  "LastPlaylistPath": null,
  "TtsVoice": "af_heart",
  "AnnouncementTemplate": "Now playing {title} by {artist}[year?, released in {year}]"
}
```

---

## 8. Version Roadmap

| Version | Scope                  | Key Deliverables                                                                                                         |
|---------|------------------------|--------------------------------------------------------------------------------------------------------------------------|
| 1.0     | Foundation *(current)* | Stream plays, ICY metadata detected, template voiceover mixed with ducking. Kokoro TTS in-process. Minimal Avalonia UI. |
| TBD     | AI Commentary          | Ollama / OpenAI commentary mode. Configurable personality.                                                               |
| TBD     | Conversation           | User can ask questions about the current track via text input.                                                           |
| TBD     | Multi-Station          | Station list, favourites, per-station personality presets.                                                               |

Future version numbers will be decided when the scope of each increment is clear.

---

## 9. Key Design Decisions

### 9.1 Why Silk.NET.OpenAL over NAudio

NAudio's audio output layer (WasapiOut, WaveOutEvent) is Windows-only. OpenAL via Silk.NET is genuinely
cross-platform and natively supports multiple simultaneous audio sources with per-source gain control, making ducking
a first-class operation.

### 9.2 Why Sherpa-ONNX over Piper

Piper is a standalone executable. Calling it as a subprocess means it cannot participate in the same OpenAL audio
context, making clean ducking impossible. Sherpa-ONNX runs entirely in-process via a C# NuGet package.

### 9.3 Why Avalonia over WPF

WPF is Windows-only. Avalonia provides a near-identical XAML-based development experience while targeting Windows,
macOS, and Linux from a single codebase.

### 9.4 Why Last.fm Instead of MusicBrainz for Track Metadata

MusicBrainz text search was designed for tagging files you already own, not for matching radio ICY strings. It
frequently surfaces compilation releases for popular tracks. The tools that achieve high match rates (MusicBrainz
Picard, beets) do so via AcoustID acoustic fingerprinting — not available when you have only an ICY string.

Last.fm `track.getInfo` was designed for exactly this use case: resolving a playback event (artist + title) to rich
metadata in a single call.

### 9.5 Why WikidataArtistService Is Self-Contained

Artist image resolution chains through three external services (MusicBrainz → Wikidata → Wikimedia). Keeping this
chain in one self-contained service means any future replacement for `LastFmMetaService` only needs to supply an
MBID string — it does not need to know anything about how images are resolved.

### 9.6 Single AI Client for All Commentary Backends

All AI commentary backends share a single HTTP client targeting the OpenAI chat completions endpoint format.
Switching from a local Ollama model to a cloud provider requires only a settings change, not a code change.

---

## 10. Open Questions for Future Versions

- AcoustID / Chromaprint fingerprinting as a fallback when ICY metadata is absent or incorrect.
- Conversation mode context management — how many tracks to retain in the AI context window before summarising.
- Package distribution strategy per platform (Windows installer, macOS `.app`, Linux AppImage / Flatpak).
