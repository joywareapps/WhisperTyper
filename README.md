# WhisperTyper

Hold a hotkey, speak, release — your words appear at the cursor. Local, private, GPU-accelerated.

WhisperTyper runs [OpenAI Whisper](https://github.com/openai/whisper) entirely on your machine using [Const-me's DirectCompute implementation](https://github.com/Const-me/Whisper), with no cloud, no subscription, and no data leaving your PC. It supports NVIDIA, AMD, and Intel GPUs out of the box via DirectCompute (Direct3D 11).

![WhisperTyper screenshot](docs/screenshot.png)

## Features

- **Hold-to-record** — hold a configurable hotkey while speaking, release to transcribe
- **Types at cursor** — output appears wherever your cursor is, in any app
- **GPU-accelerated** — runs on NVIDIA, AMD, and Intel GPUs via DirectCompute; falls back to CPU automatically
- **Local & private** — no internet connection required after setup
- **GGML model support** — works with any Whisper GGML `.bin` model (base, small, medium, large, turbo)
- **Modern dark UI** — system tray, minimize to background, settings persist between sessions

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or SDK to build from source)
- A GPU with DirectCompute support (Direct3D 11) — or CPU fallback
- A Whisper GGML model file (`.bin`) — see [Downloading a model](#downloading-a-model) below

## Downloading a model

WhisperTyper does not bundle a model. Download one from [Hugging Face](https://huggingface.co/ggerganov/whisper.cpp):

| Model | Size | Notes |
|-------|------|-------|
| `ggml-base.bin` | 142 MB | Fast, decent accuracy |
| `ggml-small.bin` | 466 MB | Good balance |
| `ggml-large-v3-turbo.bin` | 1.5 GB | Best accuracy, recommended |

Place the `.bin` file anywhere on your machine. WhisperTyper will scan `C:\Tools\whisper\models` automatically, or you can browse for any path.

## Building from source

```
git clone https://github.com/joyware/WhisperTyper.git
cd WhisperTyper
dotnet build -c Release
```

The native `Whisper.dll` (DirectCompute inference engine) is downloaded automatically via the [WhisperNet](https://www.nuget.org/packages/WhisperNet) NuGet package on first build.

## Usage

1. Launch WhisperTyper
2. Select your model file, GPU, and microphone from the dropdowns
3. Wait for the status indicator to turn green ("Ready")
4. Click into any text field in any application
5. Hold your hotkey (default: **Caps Lock**), speak, then release
6. The transcribed text appears at your cursor

WhisperTyper minimizes to the system tray — it stays active in the background while you work.

## Hotkey options

| Key | Notes |
|-----|-------|
| Caps Lock | Default; Caps Lock toggle is suppressed while in use |
| Scroll Lock | Good alternative if Caps Lock is needed |
| Left Alt | Suppresses Alt menu activation |
| Left Ctrl | |
| F9 / F10 | Function keys |

## How it works

- Audio is captured via [WASAPI](https://learn.microsoft.com/en-us/windows/win32/coreaudio/wasapi) (NAudio) while the hotkey is held
- On release, the audio is batch-transcribed by Whisper running on the GPU via DirectCompute
- The result is injected at the cursor using the Windows `SendInput` API with `KEYEVENTF_UNICODE` — works in any application, supports full Unicode

## Dependencies

| Package | Purpose |
|---------|---------|
| [WhisperNet 1.12.0](https://www.nuget.org/packages/WhisperNet) | Whisper inference via Const-me's DirectCompute DLL |
| [NAudio 2.2.1](https://www.nuget.org/packages/NAudio) | WASAPI audio capture |

## License

[MIT](LICENSE) — © 2025 Joyware
