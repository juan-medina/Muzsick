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

Commentary is generated in one of two modes controlled by the `CommentaryMode` setting.

**Template mode** generates commentary from a user-editable template with token substitution. Supported tokens:
`{title}`, `{artist}`, `{album}`, `{year}`, `{genre}`. Optional clauses (`[token?, text]`) are silently dropped
when the guarded token is empty, keeping announcements natural when enrichment data is partial.

The default template is:

```
Now playing {title} by {artist}[year?, released in {year}]
```

**AI mode** sends a user-editable prompt to a locally running Ollama instance (OpenAI-compatible HTTP API). The
prompt receives enriched track metadata as context. The model's response is stripped of any markdown formatting
before being passed to TTS. If the AI request fails or times out, commentary automatically falls back to the
template. The recommended default model is `gemma3:4b` — it has no thinking mode and responds in 3–5 seconds on
typical consumer hardware.

The announcement template is always required regardless of mode. In AI mode it acts as the fallback used when the
AI is unavailable.

### 5.6 Commentary Timing

Commentary fires at the start of a new song. The application waits approximately 3 seconds after the ICY event
before beginning metadata fetch and TTS synthesis, to avoid interrupting a song that is mid-fade or mid-intro. If a
new track arrives before the previous voiceover completes, the previous work is cancelled.

While the settings window is open, live commentary is fully suspended. Track changes still update the UI but no
commentary is generated or played. This ensures a preview in progress is never interrupted by a live track change.

---

## 6. User Interface

The Avalonia UI is intentionally minimal — the audio experience is the product; the UI is a control surface.

| Element               | Description                                                                                                                                                                  |
|-----------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Artist image          | Circular image in the header. XAML silhouette placeholder while loading. Clickable — opens the artist's Last.fm page. Only active once metadata has been resolved.           |
| Album art             | Square cover art in the main area. XAML vinyl-record placeholder shown while loading. Clickable — opens the Last.fm album page. Only active once metadata has been resolved. |
| Track title           | Large, bold. Truncated with ellipsis if too long. Clickable — opens the Last.fm track page. Only active once metadata has been resolved.                                     |
| Artist name           | Secondary text below the title. Clickable — opens the Last.fm artist page. Only active once metadata has been resolved.                                                      |
| Album name            | Tertiary text below the artist name. Shows album and year when available. Clickable — opens the Last.fm album page. Only active once metadata has been resolved.             |
| Play / Pause          | Toggles stream playback.                                                                                                                                                     |
| Replay announcement   | Replays the last generated voiceover through the audio mixer with full ducking. Disabled until the first announcement of the session has played.                             |
| Volume slider         | Controls OpenAL master output gain (0–100%). Persisted across sessions.                                                                                                      |
| Settings button       | Opens the settings window. While open, live track commentary is suspended — no announcements play until the window is closed.                                                |
| About button          | Opens the about window with version info and attribution.                                                                                                                    |
| Update/warning banner | Appears below the header when there is a system message — e.g. "⚠ AI commentary unavailable — using template". Hidden when empty.                                            |

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
  "CommentaryMode": "Template",
  "AnnouncementTemplate": "Now playing {title} by {artist}[year?, released in {year}]",
  "AiPrompt": "You are an enthusiastic radio DJ. Give a single sentence on-air intro for the next song. Track info: {context}. Respond with only the intro sentence, nothing else."
}
```

### 7.1 Settings Window Layout

The settings window is organised into sections top to bottom:

**Last.fm API Key** — required for metadata and artwork enrichment.

**Announcer Voice** — dropdown of available Kokoro voices. Applies to both template and AI commentary modes.

**Commentary Mode** — toggle between `Template` and `AI`. Controls which commentary generator runs on track change.

**Announcement Template** — always visible and always required regardless of mode. In AI mode this is labelled
*"Fallback template — used when AI commentary is unavailable."* Supports token substitution with live syntax
highlighting in the editor. Includes a Reset button to restore the default template.

**AI Prompt** — visible only when AI mode is selected. Required when AI mode is active. Free-form system prompt
sent to the local AI model. Track metadata is injected as `{context}` at runtime.

> A future version will add a prompt library with built-in personality presets and save/load functionality. This is
> not in scope for the current version.

**Preview** — a single preview section shared across both modes, always visible. Behaviour is identical regardless
of mode: generates commentary using the currently active mode, synthesises it through TTS, and plays it. A timer
shows elapsed time so the user can assess whether their prompt and model combination is fast enough for live radio
use. A cancel button is always visible during any active preview. Guidance text below the preview reads:
*"For live radio, aim for under 5 seconds. Simpler prompts and smaller models respond faster."*

### 7.2 Preview States

The preview control cycles through the following states:

| State        | Label shown         | Timer      | Cancel visible |
|--------------|---------------------|------------|----------------|
| Idle         | "Preview"           | —          | No             |
| Generating   | "Generating… 3.2s"  | Ticking up | Yes            |
| Synthesising | "Synthesising…"     | Paused     | Yes            |
| Playing      | "Playing…"          | Paused     | Yes            |
| Done         | "Generated in 4.1s" | Final time | No             |
| Cancelled    | "Preview"           | —          | No             |
| Failed       | Error message (red) | —          | No             |

In Template mode the Generating state is near-instant. In AI mode it reflects actual Ollama response time.

### 7.3 Validation Rules and Error Messages

The Save button is disabled until all validation passes. Errors are shown inline below the relevant field in red.

| Condition                         | Error message                                        |
|-----------------------------------|------------------------------------------------------|
| Last.fm API key is empty          | "An API key is required to load track metadata."     |
| Announcement template is empty    | "The announcement template cannot be empty."         |
| AI prompt is empty (AI mode only) | "An AI prompt is required when AI mode is selected." |

### 7.4 Preview Error Messages

Preview failures are shown inline below the preview control in red. They clear when the user starts a new preview.

| Condition                         | Message shown                                                         |
|-----------------------------------|-----------------------------------------------------------------------|
| Ollama not running or unreachable | "AI unavailable — make sure Ollama is running."                       |
| AI request timed out              | "AI took too long to respond. Try a simpler prompt or smaller model." |
| AI returned empty response        | "AI returned an empty response. Check your prompt."                   |
| TTS synthesis failed              | "Voice synthesis failed. Check the TTS model is installed."           |

### 7.5 Settings Window Behaviour

- While the settings window is open, live track commentary is fully suspended. Track changes still update the main
  window UI (title, artwork, metadata) but no commentary is generated or played. This ensures a preview in progress
  is never interrupted by a live track change.
- Closing the window cancels any active preview immediately.
- Cancel discards all unsaved changes. On first run, Cancel shuts down the application.

---

## 8. Version Roadmap

| Version | Scope          | Key Deliverables                                                                                                        |
|---------|----------------|-------------------------------------------------------------------------------------------------------------------------|
| 1.0     | Foundation     | Stream plays, ICY metadata detected, template voiceover mixed with ducking. Kokoro TTS in-process. Minimal Avalonia UI. |
| 1.1     | AI Commentary  | Ollama commentary mode. AI prompt editing in settings. Template/AI mode toggle. Preview with timer.                     |
| TBD     | Prompt Library | Built-in personality presets. Save/load custom prompts.                                                                 |
| TBD     | Conversation   | User can ask questions about the current track via text input.                                                          |
| TBD     | Multi-Station  | Station list, favourites, per-station personality presets.                                                              |

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

### 9.7 Commentary Suspended While Settings Window Is Open

Rather than attempting to coordinate between a live track change and an in-progress preview, commentary is simply
suspended while the settings window is open. This avoids all race conditions and gives the user a stable environment
for editing and testing their prompt. The radio stream continues playing normally — only the voiceover pipeline is
paused.

### 9.8 Preview Timer as User Education

The elapsed time shown during preview is not just feedback — it is the primary mechanism by which users learn
whether their chosen model and prompt are suitable for live radio. A user who sees "Generated in 22s" immediately
understands the problem without needing documentation.

---

## 10. Open Questions for Future Versions

- AcoustID / Chromaprint fingerprinting as a fallback when ICY metadata is absent or incorrect.
- Conversation mode context management — how many tracks to retain in the AI context window before summarising.
- Package distribution strategy per platform (Windows installer, macOS `.app`, Linux AppImage / Flatpak).
