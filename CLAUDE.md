# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

For architecture, technology choices, and design decisions see [`docs/DESIGN.md`](docs/DESIGN.md).

## Project

Muzsick is a cross-platform desktop radio companion. Built with C# / .NET 9, Avalonia UI, LibVLCSharp,
Silk.NET.OpenAL, and Sherpa-ONNX (Kokoro TTS). Designed and built by one developer.

## Build Commands

```powershell
dotnet restore src/Muzsick/Muzsick.csproj
dotnet build src/Muzsick/Muzsick.csproj
dotnet run --project src/Muzsick/Muzsick.csproj
dotnet publish src/Muzsick/Muzsick.csproj -c Release
```

## Principles

- **YAGNI** — only implement what is needed for the current scope.
- No speculative abstractions. Add interfaces when there is more than one implementation, not before.
- No exceptions for control flow. Prefer returning `null` or a result type for expected failures.
- Do not block the UI thread. Audio synthesis and network calls are always `async`/`await`.
- Comments only for non-obvious logic. No change-log comments in code.

## Naming Conventions

- `PascalCase` — types, methods, properties, events.
- `camelCase` — local variables, parameters.
- `_camelCase` — private fields, including `const` and `static readonly`.
- Namespaces match folder structure exactly, rooted at `Muzsick`.

## File Headers

Every `.cs` file:

```csharp
// SPDX-FileCopyrightText: 2026 Juan Medina
// SPDX-License-Identifier: MIT
```

Every `.axaml` file (before the root element):

```xml
<!--
    SPDX-FileCopyrightText: 2026 Juan Medina
    SPDX-License-Identifier: MIT
-->
```

## Formatting

- Tabs for indentation, not spaces.
- Allman brace style — opening brace on its own line.

## Error Handling

Prefer returning `null` or a result type over throwing for expected failures (e.g. metadata not found, stream drop).
Log errors at the point where they are handled, not where they originate. Do not swallow exceptions silently.

## Logging

`Microsoft.Extensions.Logging`. Log levels: `Debug` for internal state, `Information` for user-visible events,
`Warning` for recoverable issues, `Error` for failures requiring attention.

## Configuration

Settings live in `settings.json` in the application data folder. No registry entries. API keys are never committed
to git.

## Dependencies

All dependencies are managed via NuGet. The Kokoro model files are downloaded automatically by MSBuild on the first
build — no manual setup required. The `models/` folder is gitignored.

## Testing

No formal test suite. Testing is manual against a live radio stream.
