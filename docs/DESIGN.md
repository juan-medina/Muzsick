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
┌─────────────────────────────────────────────────────────────┐
│                      C# Application                          │
│                                                              │
│  ┌──────────────────┐   ICY metadata event                  │
│  │  LibVLCSharp     │──────────────────────────┐            │
│  │  Stream Player   │                          ▼            │
│  └────────┬─────────┘             ┌────────────────────┐    │
│           │ PCM audio             │  Metadata Service  │    │
│           ▼                       │  MusicBrainz API   │    │
│  ┌──────────────────┐             └─────────┬──────────┘    │
│  │  Silk.NET.OpenAL │◄────────────┐         ▼               │
│  │  Mixer + Output  │  PCM buffer │  ┌────────────────┐     │
│  │  + Volume Duck   │◄────────────┘  │  Sherpa-ONNX   │     │
│  └────────┬─────────┘                │  Kokoro TTS    │     │
│           │                          └──────┬─────────┘     │
│           ▼                                  ▲              │
│       Speakers                      ┌────────┴─────────┐    │
│                                     │  Commentary Gen  │    │
│  ┌─────────────────────────────┐    │  Template / AI   │    │
│  │  Avalonia UI                │    └──────────────────┘    │
│  │  Song · Artist · Album Art  │                            │
│  │  Volume · AI Toggle         │                            │
│  └─────────────────────────────┘                            │
└─────────────────────────────────────────────────────────────┘
```

**Key design principle:** Silk.NET.OpenAL is the single audio output point. LibVLCSharp decodes the stream and feeds
raw PCM data into OpenAL as one audio source. When a voiceover is ready, it is loaded as a second OpenAL source into
the same context. Ducking is achieved by adjusting the gain on the stream source — LibVLCSharp never touches the
output device directly.

---

## 4. Technology Stack

| Component           | Technology                        | Notes                              |
|---------------------|-----------------------------------|------------------------------------|
| Language & Runtime  | C# / .NET 9                       | Cross-platform                     |
| UI Framework        | Avalonia UI                       | Windows, macOS, Linux              |
| Stream Playback     | LibVLCSharp + VideoLAN.LibVLC     | NuGet + native libs                |
| Audio Mixing/Output | Silk.NET.OpenAL                   | Cross-platform, replaces NAudio    |
| Text-to-Speech      | Sherpa-ONNX (C# bindings)         | In-process, no subprocess          |
| TTS Voice Model     | Kokoro-82M (ONNX export)          | ~80 MB, shipped with installer     |
| Metadata            | MetaBrainz.MusicBrainz            | NuGet package                      |
| Cover Art           | MusicBrainz Cover Art Archive     | HTTP via HttpClient                |
| AI Commentary       | OpenAI-compatible HTTP API        | Optional, user-configured endpoint |
| Configuration       | System.Text.Json — settings.json  | No registry entries                |

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

### 5.4 Metadata — MusicBrainz

When a song change is detected, the artist name and track title are extracted from the ICY metadata string. These are
used to query the MusicBrainz API to retrieve release year, album name, genre tags, and the MusicBrainz ID. The ID is
then used to fetch album artwork from the Cover Art Archive.

All metadata results are cached in a `ConcurrentDictionary` keyed on `artist+title` to avoid redundant API calls for
repeated tracks. Cache entries expire after 24 hours.

> ICY metadata reliability varies by station. Some stations report incorrect or missing artist/title data. A future
> version may add AcoustID audio fingerprinting as a fallback identification method.

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

| Element               | Description                                                                   |
|-----------------------|-------------------------------------------------------------------------------|
| Album art             | Square image (300×300 px), loaded from Cover Art Archive. Placeholder shown while loading. |
| Track title           | Large, bold text. Truncated with ellipsis if too long.                        |
| Artist name           | Smaller, secondary text below the title.                                      |
| Play / Pause button   | Toggles stream playback.                                                      |
| Volume slider         | Controls the OpenAL master output gain (0–100%).                              |
| AI Commentary toggle  | Switches between template and AI commentary modes.                            |
| Settings button       | Opens a settings panel for stream URL, AI endpoint, API key, voice selection. |

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

```
src/Muzsick/
├── App.axaml
├── App.axaml.cs
├── Program.cs
├── ViewLocator.cs
│
├── Audio/
│   ├── StreamPlayer.cs          # LibVLCSharp wrapper, ICY event source
│   ├── AudioMixer.cs            # OpenAL context, source management, ducking
│   └── DuckingController.cs     # Volume fade in/out timing
│
├── Tts/
│   ├── ITtsBackend.cs           # Interface: Task<byte[]> SynthesizeAsync(string)
│   ├── KokoroTtsBackend.cs      # Sherpa-ONNX / Kokoro implementation
│   └── AzureTtsBackend.cs       # Optional cloud backend (V1+)
│
├── Metadata/
│   ├── IcyMetadataParser.cs     # Parses Artist - Title from ICY string
│   ├── MusicBrainzService.cs    # Metadata + cover art fetch + cache
│   └── TrackInfo.cs             # Data model
│
├── Commentary/
│   ├── ICommentaryGenerator.cs
│   ├── TemplateCommentary.cs    # Version 0 — no AI
│   ├── AiCommentary.cs          # Version 1 — OpenAI-compatible
│   └── Prompts.cs               # System + user prompt templates
│
├── Config/
│   ├── AppSettings.cs           # Settings model
│   └── SettingsManager.cs       # Load / save settings.json
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

| Version | Scope            | Key Deliverables                                                                                      |
|---------|------------------|-------------------------------------------------------------------------------------------------------|
| V0      | Foundation       | Stream plays, ICY metadata detected, template voiceover mixed with ducking. Kokoro TTS in-process. Minimal Avalonia UI. |
| V1      | AI Commentary    | Ollama / OpenAI commentary mode. Configurable personality. Metadata enrichment via MusicBrainz. Settings UI. |
| V2      | Conversation     | User can ask questions about the current track or artist via text input. Context window maintains last N tracks. |
| V3      | Multi-Station    | Station list, favourites, per-station AI personality presets. Plugin system for custom commentary scripts. |

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

---

## 11. Open Questions for Future Versions

- AcoustID / Chromaprint fingerprinting as a fallback when ICY metadata is absent or incorrect.
- Conversation mode context management — how many tracks to retain in the AI context window before summarising.
- Whether to surface a waveform visualiser in the UI (V2+ consideration).
- Package distribution strategy per platform (Windows installer, macOS `.app`, Linux AppImage / Flatpak).
