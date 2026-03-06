# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Muzsick** is a cross-platform desktop radio companion that plays live internet radio streams and enriches the
listening experience with AI-generated DJ-style commentary. When a song changes, the application detects the track
transition, retrieves rich metadata, generates a commentary script, synthesises a voiceover locally using an on-device
neural TTS engine, and seamlessly mixes it into the ongoing audio stream — all without interrupting playback.

Built with C# / .NET 9, Avalonia UI, LibVLCSharp, Silk.NET.OpenAL, and Sherpa-ONNX (Kokoro TTS). Runs fully offline
after initial setup. Cloud AI services are supported as optional enhancements when the user provides API keys.

Designed and built by one developer. Follow YAGNI — only implement what's needed for the current scope.

## Build Commands

```powershell
# Restore dependencies
dotnet restore src/Muzsick/Muzsick.csproj

# Build
dotnet build src/Muzsick/Muzsick.csproj

# Run
dotnet run --project src/Muzsick/Muzsick.csproj

# Build release
dotnet publish src/Muzsick/Muzsick.csproj -c Release
```

## Architecture

### Audio Pipeline

The audio pipeline is the core of the application. It follows a strict source → mixer → output model:

```
LibVLCSharp (radio PCM)  ─┐
                           ├→  Silk.NET.OpenAL  →  Speakers
Sherpa-ONNX (TTS PCM)   ─┘
```

- **LibVLCSharp** is a PCM source only. It decodes the radio stream and surfaces ICY metadata events. It never touches
  the output device directly.
- **Silk.NET.OpenAL** is the single audio output point. It receives PCM from both sources, handles mixing, and manages
  volume per source. Ducking is achieved by adjusting the gain on the radio source while the TTS source plays.
- **Sherpa-ONNX / Kokoro** synthesises TTS entirely in-process, returning raw PCM bytes.

### Commentary Flow

1. ICY metadata event fires — song has changed.
2. Wait ~3 seconds for stream to stabilise.
3. Fetch track metadata from Last.fm. Artist image from Wikidata via WikidataArtistService.
4. Generate commentary (template or AI mode).
5. Synthesise voiceover via Kokoro TTS.
6. Duck radio volume to 20% over 500ms.
7. Play voiceover through OpenAL.
8. Fade radio back to 100% over 800ms.

### Version Roadmap

| Version | Scope                  | Key Deliverables                                                              |
|---------|------------------------|-------------------------------------------------------------------------------|
| V0      | Foundation             | Stream plays, ICY metadata detected, template voiceover mixed with ducking.   |
| V1      | AI Commentary          | Ollama / OpenAI commentary. Metadata via Last.fm + Wikidata. Settings UI.     |
| V2      | Conversation           | User can ask questions about the current track via text input.                |
| V3      | Multi-Station          | Station list, favourites, per-station personality presets.                    |

Only implement what the current version requires. Do not anticipate future versions.

## Project Structure

```
src/Muzsick/
├── App.axaml
├── App.axaml.cs
├── Program.cs
├── ViewLocator.cs
│
├── Audio/               # LibVLCSharp stream wrapper, OpenAL mixer, ducking
├── Tts/                 # ITtsBackend interface + Kokoro implementation
├── Metadata/            # ICY parser, Last.fm + Wikidata services, TrackInfo model
├── Commentary/          # ICommentaryGenerator, template and AI implementations
├── Config/              # AppSettings model, settings.json load/save
│
├── ViewModels/          # Avalonia MVVM ViewModels
├── Views/               # Avalonia AXAML views
│
└── Models/
    └── KokoroModels/    # Kokoro ONNX model files (~80 MB, stored in Git LFS)
```

## Technology Stack

| Component        | Library                          |
|------------------|----------------------------------|
| UI Framework     | Avalonia UI (MVVM template)      |
| Stream Playback  | LibVLCSharp + VideoLAN.LibVLC    |
| Audio Output     | Silk.NET.OpenAL                  |
| TTS              | Sherpa-ONNX + Kokoro-82M (ONNX)  |
| Metadata         | Last.fm API (`track.getInfo`)    |
| Artist Images    | Wikimedia / Wikidata             |
| AI Commentary    | OpenAI-compatible HTTP (optional)|
| Configuration    | System.Text.Json — settings.json |

## Code Conventions

- **Naming**: `PascalCase` for types, methods, and properties. `camelCase` for local variables and parameters.
  `_camelCase` for private fields and constants (underscore prefix, including `const` and `static readonly`).
- **Namespaces**: Match folder structure exactly. Root namespace is `Muzsick`.
- **File headers**: Every `.cs` and `.axaml` file begins with SPDX headers:
  ```csharp
  // SPDX-FileCopyrightText: 2026 Juan Medina
  // SPDX-License-Identifier: MIT
  ```
  For `.axaml` files use XML comment syntax before the root element.
- **Interfaces**: All major subsystems are behind interfaces (`ITtsBackend`, `ICommentaryGenerator`, `IMetaService`).
  Services are instantiated in the ViewModel for now — a DI container is not yet wired up.
- **Async**: Audio synthesis and metadata fetch are always `async`/`await`. Do not block the UI thread.
- **No**: exceptions for control flow, `Thread.Sleep`, fire-and-forget tasks without handling.
- **Comments**: Only for non-obvious logic. No change-log comments in code.
- **Tabs**: Indentation uses tabs, not spaces. See `.editorconfig`.

## Error Handling

Use structured error handling with meaningful context. Prefer returning `null` or a result type over throwing for
expected failure cases (e.g. metadata not found, stream drop). Log errors at the point where they are handled, not
where they originate. Do not swallow exceptions silently.

## Logging

Use `Microsoft.Extensions.Logging` (or a thin wrapper). Log levels: `Debug` for internal state, `Information` for
user-visible events (song change, voiceover played), `Warning` for recoverable issues (metadata not found),
`Error` for failures requiring attention. Do not use logging for control flow.

## Configuration

User preferences live in `settings.json` in the application data folder. No registry entries. Settings are loaded on
startup and written on any change. Sensitive values (API keys) are stored in the same file but never committed to git.

## Testing

No formal test suite. Testing is manual — run the application against a live radio stream. Pure logic units
(ICY parser, template commentary, metadata cache) may have unit tests added if they become complex enough to warrant
it, but do not add tests speculatively.

## Dependencies

All dependencies are managed via NuGet. The Kokoro ONNX model files (~80 MB) are stored in the repository via
**Git LFS** — they are tracked by `.gitattributes` (`*.onnx`, `*.bin`, `*.dat`) and are automatically downloaded
when you clone with `git lfs install` (or `git lfs pull` after a regular clone). See `Models/KokoroModels/` for
the expected file layout.

> First-time setup: run `git lfs install` once globally, then clone normally. Existing clones: run `git lfs pull`
> to fetch the model files.
