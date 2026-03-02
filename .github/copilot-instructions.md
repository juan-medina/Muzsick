# Copilot Instructions

## Project

**Muzsick** — a cross-platform desktop radio companion built with C# / .NET 9 and Avalonia UI. It plays live internet
radio streams and mixes in AI-generated DJ-style voiceovers using local TTS (Sherpa-ONNX / Kokoro). Runs fully offline
after initial setup.

## Principles

- Follow YAGNI — only implement what is needed right now, not what might be needed later.
- No speculative abstractions. Add interfaces when there is more than one implementation, not before.
- No exceptions for control flow. Prefer returning `null` or a result type for expected failures.
- Do not block the UI thread. Audio synthesis and network calls are always `async`/`await`.
- Comments only for non-obvious logic. No change-log comments in code.

## Naming Conventions

- `PascalCase` — types, methods, properties, events
- `camelCase` — local variables, parameters
- `_camelCase` — private fields (underscore prefix, no `this.`)
- `UPPER_CASE` — constants
- Namespaces match folder structure exactly, rooted at `Muzsick`

## File Headers

Every `.cs` file must start with:

```csharp
// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT
```

Every `.axaml` file must start with (before the root element):

```xml
<!--
    SPDX-FileCopyrightText: 2026 Juan Medina
    SPDX-License-Identifier: MIT
-->
```

## Formatting

- Tabs for indentation, not spaces.
- Allman brace style — opening brace on its own line.
- One blank line between members. Two blank lines between type declarations.

## Architecture Notes

- **LibVLCSharp** is a PCM source only — it decodes the radio stream and fires ICY metadata events. It never writes
  to an audio device directly.
- **Silk.NET.OpenAL** is the single audio output point — it receives PCM from the radio stream and TTS engine,
  handles mixing, and controls per-source volume for ducking.
- **Sherpa-ONNX / Kokoro** synthesises TTS entirely in-process and returns raw PCM bytes.
- All major subsystems are behind interfaces (`ITtsBackend`, `ICommentaryGenerator`). Inject, don't instantiate.
- Settings live in `settings.json` in the app data folder. No registry entries. API keys are never committed to git.

## Technology Stack

| Role            | Library                        |
|-----------------|--------------------------------|
| UI              | Avalonia UI                    |
| Stream Playback | LibVLCSharp                    |
| Audio Output    | Silk.NET.OpenAL                |
| TTS             | Sherpa-ONNX + Kokoro-82M       |
| Metadata        | MetaBrainz.MusicBrainz         |
| AI Commentary   | OpenAI-compatible HTTP         |
| Config          | System.Text.Json               |
