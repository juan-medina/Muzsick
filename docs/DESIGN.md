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
- No support for DRM-protected streams in V1.
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
│  │  Volume · AI Toggle         │                           │
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
| TTS Voice Model     | Kokoro-82M (ONNX export)         | ~80 MB, shipped with installer     |
| Track Metadata      | Last.fm API (`track.getInfo`)    | HTTP via HttpClient                |
| Artist Images       | Wikimedia Commons / Wikidata     | HTTP via HttpClient                |
| AI Commentary       | OpenAI-compatible HTTP API       | Optional, user-configured endpoint |
| Configuration       | System.Text.Json — settings.json | No registry entries                |

---

## 5. Component Details

### 5.1 Stream Playback — LibVLCSharp

LibVLCSharp provides the official .NET bindings for libvlc, the same engine used by the VLC media player. It supports
`.pls` and `.m3u` playlist formats, handles reconnection on stream drop, and surfaces ICY metadata change events that
signal a song transition.

LibVLCSharp is configured to output decoded PCM audio to a callback that feeds data into an OpenAL streaming buffer,
rather than directly to a sound device. This gives OpenAL full control over the final output mix.

### 5.2 Audio Mixing and Ducking — Silk.NET.OpenAL

OpenAL manages two audio sources: the continuous radio stream and an on-demand TTS source. Each source has its own
gain value that can be adjusted independently at any time.

When a voiceover is ready, the following sequence executes:

1. Fade radio source gain down to 20% over 500ms.
2. Begin playing the TTS PCM buffer through the TTS source.
3. When TTS playback completes, fade radio source gain back to 100% over 800ms.

This is the key advantage of a single audio context. Both sources are mixed by OpenAL natively — no separate routing
layer or inter-process audio is needed.

### 5.3 Text-to-Speech — Sherpa-ONNX + Kokoro

Sherpa-ONNX provides a C# NuGet package that wraps an ONNX runtime, enabling fully in-process neural TTS inference.
The Kokoro-82M model produces natural-sounding speech well suited to DJ-style announcements and runs in real time on
a modest CPU.

The model file (~80 MB) is bundled with the application installer. No internet connection is required for TTS
synthesis. The TTS backend is abstracted behind an interface, allowing alternative backends to be swapped in without
changing the mixing logic.

```csharp
public interface ITtsBackend
{
    Task<byte[]> SynthesizeAsync(string text); // Returns PCM WAV bytes
}
```

### 5.4 Metadata — Last.fm + Wikidata

When a song change is detected, the artist name and track title extracted from the ICY metadata are used to look up
enrichment data from two sources.

**Why not MusicBrainz**

MusicBrainz was evaluated first. The fundamental problem is that its text search was designed for tagging files you
already own, not for matching radio ICY strings. Searching by `artist + title` frequently surfaces compilation
releases rather than original studio albums, which makes selecting the right cover art and year unreliable. Even
MusicBrainz Picard — the official tagger — has this known, unresolved problem; it adds manual sliders for users to
tune release preferences. The tools in the ecosystem that achieve high match rates (Picard with AcoustID, beets) do
so via acoustic fingerprinting, which is not available when you have only an ICY string. MusicBrainz text search
is the wrong tool for this job.

**Track metadata — Last.fm `track.getInfo`**

The Last.fm `track.getInfo` endpoint is designed exactly for `artist + title` lookups from playback events. It
returns album name, release year, genre tags, and album art URLs in a single call, without requiring compilation
filtering or release scoring. A free API key is required; no account is needed beyond registration.

The lookup flow is:

1. Call `track.getInfo` with `artist` and `track` from the ICY string.
2. If found, extract album name, cover art URL, year, and top tags.
3. If not found (rare for charted tracks), leave enrichment fields empty — no fallback to a second source.

**Artist images — WikidataArtistService**

Last.fm does not provide artist portrait images, but `artist.getInfo` returns the artist's MusicBrainz ID (MBID) as
a plain string field in its JSON response. That MBID is the key to the artist image chain.

`WikidataArtistService` owns the full resolution flow and has no dependency on any other metadata service:

1. Receive the MBID string from the caller (`LastFmMetaService`).
2. Call the MusicBrainz REST API directly (`/ws/2/artist/{mbid}?inc=url-rels`) to retrieve the artist's Wikidata URL.
3. Extract the Wikidata QID from the URL and call the Wikidata API (P18 claim) to get the image filename.
4. Build the Wikimedia Commons thumbnail URL from the filename using the MD5-based path scheme.

`WikidataArtistService` uses the `MetaBrainz.MusicBrainz` NuGet package for the url-rels lookup in step 2. This is
intentional — the dependency lives here rather than in the track metadata service, keeping the artist image chain
self-contained. Any future replacement for `LastFmMetaService` only needs to supply an MBID string.

> Last.fm stores MBIDs as a cross-reference to MusicBrainz. This means the artist image path has a functional
> dependency on both Last.fm and MusicBrainz remaining in sync. In practice both are long-running open projects and
> this chain is stable, but it is worth noting as an architectural assumption.

The two-source design is reflected in the shared interface:

```csharp
public interface IMetaService
{
    // Enriches track metadata (album, art, year, tags) via Last.fm
    // and artist image via Wikidata.
    // Returns originals unchanged if nothing is found.
    Task<(TrackInfo Track, ArtistInfo Artist)> EnrichAsync(TrackInfo track);
}
```

All results are cached in a `ConcurrentDictionary` keyed on `artist+title` to avoid redundant API calls for repeated
tracks. Cache entries expire after 24 hours.

**Track lookup strategy**

ICY metadata from radio stations is unreliable in several ways: artist names are comma-joined when multiple credits
exist, titles may include featured artist decorators or version labels, compilations can be returned instead of the
original release, and some stations censor or alter track titles entirely. A single `track.getInfo` call is not
enough — `LastFmMetaService` uses a multi-stage strategy to maximise the chance of finding a rich result:

```
ICY string: "Wilkinson, ILIRA, iiola, Tom Cane" — "Infinity (feat. ILIRA, iiola & Tom Cane)"

Stage 1 — Exact ICY artist + exact ICY title
  → track.getInfo(artist="Wilkinson, ILIRA, iiola, Tom Cane", track="Infinity (feat. ...)")
  → Last.fm matches the comma-joined name as an obscure artist entry, returns track with no album/art
  → result is not rich, continue

Stage 2 — Primary artist + exact ICY title
  → split artist on first comma → "Wilkinson"
  → track.getInfo(artist="Wilkinson", track="Infinity (feat. ILIRA, iiola & Tom Cane)")
  → full album + cover art returned  ✓  stop here

─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─

ICY string: "Roddy Ricch" — "The Box"

Stage 1 — Exact match
  → track.getInfo(artist="Roddy Ricch", track="The Box", autocorrect=1)
  → autocorrect drifts to compilation "Just Hits" (album.artist = "Various Artists")
  → IsCompilation = true, retry without autocorrect

Stage 1b — Same args, autocorrect=0
  → track.getInfo(artist="Roddy Ricch", track="The Box", autocorrect=0)
  → returns original album "Please Excuse Me for Being Antisocial"  ✓  stop here

─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─

ICY string: "Zolita" — "Somebody I Did Once"   (station censored the real title)

Stage 1 — Exact match → error 6, track not found
Stage 2 — Primary artist same as artist, skip
Stage 3 — Clean title → no decorators to strip, cleaned == original, skip
Stage 4 — Primary + cleaned, skip (same as stage 1)
Stage 5 — track.search fallback
  → track.search(artist="Zolita", track="Somebody I Did Once")
  → top result: "Somebody I F*cked Once" by Zolita
  → artist matches, corrected title differs from original
  → track.getInfo(artist="Zolita", track="Somebody I F*cked Once")
  → album + cover art returned  ✓
```

Each stage only runs if the previous one returned null or a result with no album and no cover art (`IsRich = false`).
A partial result (MBID found but no art) is kept as a fallback so that at minimum the artist image can still be
resolved even when no rich track result exists. The original ICY title is always preserved in the UI — the enriched
album name is displayed separately and the displayed title is never overwritten.

**Compilation detection**

`autocorrect=1` on Last.fm can drift to the most-played version of a track, which is often a compilation rather
than the artist's own release. `LastFmMetaService` reads the `album.artist` field in the response: if it equals
`"Various Artists"` the result is flagged as a compilation and the same call is retried with `autocorrect=0`, which
forces an exact artist match.

**Title cleaning**

If all exact-title attempts fail, the title is stripped of common decorator patterns before retrying:

- Featured artists: `ft.`, `feat.`, `featuring` (and everything after)
- Parenthetical suffixes: `(Radio Edit)`, `(Remastered)`, `(Live)`, `(Acoustic)`, etc.
- Dash suffixes: `- Radio Edit`, `- Remix`, etc.
- Bracket suffixes: `[from ...]`, `[original ...]`

**Artist MBID resolution**

`track.getInfo` includes the artist's MusicBrainz MBID in the response for most artists. For smaller or indie
artists Last.fm sometimes omits it. In that case `LastFmMetaService` makes a secondary call to `artist.getInfo`
by name to retrieve the MBID, then passes it to `WikidataArtistService` for the image lookup chain.

### 5.5 Commentary Generation

Commentary is generated in two modes, switchable at runtime.

**Version 0 — Template Mode**

A set of hand-written templates are filled with metadata values. No AI required. Example output:
*"Up next — Don't Stop Me Now by Queen, from the 1978 album Jazz. A classic."*

**Version 1 — AI Mode**

A structured prompt is sent to an OpenAI-compatible chat completions endpoint. The prompt includes track metadata and
instructs the model to respond with a short (2–3 sentence) DJ-style commentary in a configurable personality. The
user can point this at a local Ollama instance or any cloud endpoint.

```
POST {baseUrl}/v1/chat/completions
{
  "model": "...",
  "messages": [
    { "role": "system", "content": "You are a warm, knowledgeable radio DJ..." },
    { "role": "user",   "content": "Track: {title} by {artist}, {year}, album {album}..." }
  ],
  "max_tokens": 120
}
```

All AI commentary backends (Ollama, OpenAI, LM Studio, or any compatible API) share a single HTTP client targeting
the OpenAI chat completions format. Switching from a local model to a cloud provider requires only a settings change.

### 5.6 Commentary Timing

Commentary fires at the start of a new song, not mid-track. The application waits approximately 3 seconds after the
ICY event before beginning TTS synthesis, to avoid interrupting a song that is mid-fade or mid-intro. A minimum
inter-commentary interval (default: 3 songs) prevents back-to-back voiceovers.

---

## 6. User Interface

The Avalonia UI is intentionally minimal. The design ethos is that the audio experience is the product — the UI is
just a control surface.

| Element              | Description                                                                      |
|----------------------|----------------------------------------------------------------------------------|
| Album art            | Square image (300×300 px), loaded from Last.fm. Placeholder shown while loading. |
| Artist image         | Circular image (60×60 px) in the header. Placeholder shown while loading.        |
| Track title          | Large, bold text. Truncated with ellipsis if too long.                           |
| Artist name          | Smaller, secondary text below the title.                                         |
| Play / Pause button  | Toggles stream playback.                                                         |
| Volume slider        | Controls the OpenAL master output gain (0–100%).                                 |
| AI Commentary toggle | Switches between template and AI commentary modes.                               |
| Settings button      | Opens a settings panel for stream URL, AI endpoint, API key, voice selection.    |

Both image areas display XAML-drawn placeholders (vinyl record graphic for album art, silhouette for artist) when no
URL is available. The placeholder is replaced by the real image as soon as the metadata service returns a URL. If the
service returns nothing, the placeholder remains.

---

## 7. Configuration

User preferences are stored in a `settings.json` file in the application data folder. The file is read on startup and
written on any settings change. No registry entries are used.

```json
{
  "streamUrl": "http://example-stream.com/radio.m3u",
  "volume": 80,
  "commentary": {
    "enabled": true,
    "mode": "ai",
    "minIntervalSongs": 3,
    "personality": "warm"
  },
  "ai": {
    "baseUrl": "http://localhost:11434",
    "model": "llama3",
    "apiKey": ""
  },
  "tts": {
    "backend": "kokoro",
    "voice": "af_heart"
  }
}
```

---

## 8. Project Structure

Files marked *(planned)* do not yet exist — they represent the target architecture for upcoming versions.

```
src/Muzsick/
├── App.axaml
├── App.axaml.cs
├── Program.cs
├── ViewLocator.cs
│
├── Audio/
│   ├── StreamPlayer.cs          # LibVLCSharp wrapper, ICY event source
│   ├── AudioMixer.cs            # (planned) OpenAL context, source management, ducking
│   └── DuckingController.cs     # (planned) Volume fade in/out timing
│
├── Tts/
│   ├── ITtsBackend.cs           # (planned) Interface: Task<byte[]> SynthesizeAsync(string)
│   ├── KokoroTtsBackend.cs      # (planned) Sherpa-ONNX / Kokoro implementation
│   └── AzureTtsBackend.cs       # (planned) Optional cloud backend
│
├── Metadata/
│   ├── TrackInfo.cs             # ICY core fields + enrichment fields
│   ├── ArtistInfo.cs            # Artist name + enrichment fields (image, bio)
│   ├── IMetaService.cs          # Interface: EnrichAsync(TrackInfo)
│   ├── LastFmMetaService.cs     # IMetaService impl — Last.fm track.getInfo
│   ├── WikidataArtistService.cs # Artist image: MBID → MusicBrainz url-rels → Wikidata → Wikimedia
│   └── IcyMetadataParser.cs     # Parses Artist - Title from ICY string
│
├── Commentary/
│   ├── ICommentaryGenerator.cs  # (planned)
│   ├── TemplateCommentary.cs    # (planned) Version 0 — no AI
│   ├── AiCommentary.cs          # (planned) Version 1 — OpenAI-compatible
│   └── Prompts.cs               # (planned) System + user prompt templates
│
├── Config/
│   ├── AppSettings.cs           # (planned) Settings model
│   └── SettingsManager.cs       # (planned) Load / save settings.json
│
├── ViewModels/
│   └── MainWindowViewModel.cs
│
├── Views/
│   └── MainWindow.axaml
│
└── Models/
    └── KokoroModels/            # Kokoro ONNX model files (~80 MB, not in git)
```

---

## 9. Version Roadmap

| Version | Scope         | Key Deliverables                                                                                                        |
|---------|---------------|-------------------------------------------------------------------------------------------------------------------------|
| V0      | Foundation    | Stream plays, ICY metadata detected, template voiceover mixed with ducking. Kokoro TTS in-process. Minimal Avalonia UI. |
| V1      | AI Commentary | Ollama / OpenAI commentary mode. Configurable personality. Metadata enrichment via Last.fm + Wikidata. Settings UI.     |
| V2      | Conversation  | User can ask questions about the current track or artist via text input. Context window maintains last N tracks.        |
| V3      | Multi-Station | Station list, favourites, per-station AI personality presets. Plugin system for custom commentary scripts.              |

---

## 10. Key Design Decisions

### 10.1 Why C# over Python

Audio mixing with proper volume ducking requires in-process participation in the same audio context, which is
straightforward in C# but awkward in Python. Avalonia provides a native cross-platform UI with less boilerplate than
Python alternatives. The entire application including TTS inference can be compiled into a self-contained installer
with no user-facing runtime dependencies.

### 10.2 Why Sherpa-ONNX over Piper

Piper is a high-quality local TTS engine but is distributed as a standalone executable. Calling it as a subprocess
means it cannot participate in the same audio context, making clean ducking impossible without a separate audio
routing layer. Sherpa-ONNX provides equivalent voice quality via a proper C# NuGet package, runs entirely
in-process, and exposes async synthesis APIs that integrate cleanly with the rest of the codebase.

### 10.3 Why Silk.NET.OpenAL over NAudio

NAudio's mixing primitives are cross-platform but its audio output layer (WasapiOut, WaveOutEvent) is Windows-only.
OpenAL via Silk.NET is genuinely cross-platform — Windows, macOS, and Linux — and natively supports multiple
simultaneous audio sources with per-source gain control. This makes ducking a first-class operation rather than
something bolted on top of a Windows-only output driver. Silk.NET is a .NET Foundation project with active
maintenance and regular releases.

### 10.4 Why Avalonia over WPF

WPF is Windows-only. Avalonia provides a near-identical XAML-based development experience while targeting Windows,
macOS, and Linux from a single codebase. The Avalonia MVVM template maps directly to the application's ViewModel
architecture.

### 10.5 Single AI Client for All Backends

All AI commentary backends (Ollama, OpenAI, LM Studio, or any compatible API) share a single HTTP client targeting
the OpenAI chat completions endpoint format. The user configures a base URL and optional API key. Switching from a
local Ollama model to a cloud provider requires only a settings change, not a code change.

### 10.6 Why Last.fm Instead of MusicBrainz for Track Metadata

MusicBrainz was the first candidate. The core problem is that its search index returns too many compilation releases
for popular tracks, making it difficult to reliably select the original studio album for cover art and year data.
This is a well-known issue in the ecosystem — even MusicBrainz Picard, the official tagging tool, exposes manual
"preferred releases" sliders because it cannot solve the problem automatically. The tools that do achieve high
match rates (Picard, beets) rely on AcoustID acoustic fingerprinting, not text search. An ICY metadata string
gives us only artist and title — no audio to fingerprint.

Last.fm `track.getInfo` was designed for exactly this use case: resolving a playback event (artist + title) to rich
metadata. It returns album name, cover art, year, and tags in a single call without requiring release scoring or
compilation filtering. Artist images are not provided by Last.fm and are sourced from Wikidata via
`WikidataArtistService`.

### 10.7 Why WikidataArtistService Is Self-Contained

Artist image resolution requires a chain of three external calls: MusicBrainz url-rels lookup (MBID → Wikidata URL),
Wikidata API (QID → image filename), and Wikimedia Commons (filename → thumbnail URL). This chain is independent of
which service provides track metadata.

`WikidataArtistService` owns all three steps so that it can be called identically by any metadata service
implementation — current or future — by passing a single MBID string. This means `MusicBrainzMetaService` can be
removed entirely when `LastFmMetaService` is ready, without touching the artist image path at all. Both services
get the MBID from their respective API responses (`MusicBrainzMetaService` from the recording's artist credit,
`LastFmMetaService` from the `artist.mbid` field in Last.fm's JSON) and pass it as a plain string.

The `MetaBrainz.MusicBrainz` NuGet dependency lives in `WikidataArtistService` for the url-rels step. This is
deliberate — it keeps the dependency out of `LastFmMetaService` while still being available to the service that
needs it.

---

## 11. Open Questions for Future Versions

- AcoustID / Chromaprint fingerprinting as a fallback when ICY metadata is absent or incorrect.
- Conversation mode context management — how many tracks to retain in the AI context window before summarising.
- Whether to surface a waveform visualiser in the UI (V2+ consideration).
- Package distribution strategy per platform (Windows installer, macOS `.app`, Linux AppImage / Flatpak).
