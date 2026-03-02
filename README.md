# Muzsick

> AI-Powered Radio Companion for Windows

Muzsick plays live internet radio streams and enriches the listening experience with AI-generated DJ-style commentary — spoken aloud, mixed seamlessly into the audio, no interruptions.

Built with C# / .NET 9, WPF, NAudio, LibVLCSharp, and Sherpa-ONNX (Kokoro TTS). Runs fully offline after initial setup.

---

## Status

🚧 **Early development** — not ready for general use yet.

---

## Requirements

- Windows 10 or later
- .NET 9 SDK (for building from source)

---

## Building from Source

```bash
git clone https://github.com/juan-medina/muzsick.git
cd muzsick
dotnet build src/Muzsick/Muzsick.csproj
```

> Kokoro TTS model files (~80 MB) are not included in the repository.
> Download instructions coming soon.

---

## License

MIT © 2026 [Juan Medina](https://github.com/juan-medina)
