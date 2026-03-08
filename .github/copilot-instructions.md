# Copilot Instructions

For architecture, technology choices, and design decisions see [`docs/DESIGN.md`](docs/DESIGN.md).

## Project

**Muzsick** — a cross-platform desktop radio companion built with C# / .NET 9 and Avalonia UI. It plays live internet
radio streams and mixes in AI-generated DJ-style voiceovers using local TTS (Sherpa-ONNX / Kokoro). Designed and
built by one developer.

## Principles

- **YAGNI** — only implement what is needed right now, not what might be needed later.
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
- One blank line between members. Two blank lines between type declarations.

## Error Handling

Prefer returning `null` or a result type over throwing for expected failures. Log errors at the point where they are
handled, not where they originate. Do not swallow exceptions silently.

## Configuration

Settings live in `settings.json` in the application data folder. No registry entries. API keys are never committed
to git.
