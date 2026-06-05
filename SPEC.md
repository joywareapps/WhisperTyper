# WhisperTyper — Product Specification

**Repository:** https://github.com/joywareapps/WhisperTyper  
**Platform:** Windows 10/11 x64  
**Framework:** .NET 10, WPF  
**License:** MIT

---

## Table of Contents

1. [Overview](#1-overview)
2. [Architecture](#2-architecture)
3. [Current Features (v0.6.0)](#3-current-features-v060)
   - 3.1 [Global Hotkey Capture](#31-global-hotkey-capture)
   - 3.2 [Audio Capture](#32-audio-capture)
   - 3.3 [Whisper Transcription Engine](#33-whisper-transcription-engine)
   - 3.4 [Sentence Streaming](#34-sentence-streaming)
   - 3.5 [Keyboard Output Injection](#35-keyboard-output-injection)
   - 3.6 [Model Loading & Manager](#36-model-loading--manager)
   - 3.7 [History Log](#37-history-log)
   - 3.8 [Custom Dictionary](#38-custom-dictionary)
   - 3.9 [Filler Word Removal](#39-filler-word-removal)
   - 3.10 [Settings Persistence](#310-settings-persistence)
   - 3.11 [User Interface](#311-user-interface)
4. [Planned Features](#4-planned-features)
5. [Data & File Locations](#5-data--file-locations)
6. [Dependencies](#6-dependencies)
7. [Known Limitations](#7-known-limitations)

---

## 1. Overview

WhisperTyper is a Windows system-tray application that converts speech to text using a locally-running Whisper model and types the result directly at the active cursor position in any application.

**Core user flow:**
1. User holds a configurable hotkey
2. Application records audio via WASAPI microphone capture
3. Every 4 seconds mid-recording, complete sentences are transcribed and typed at the cursor as they are recognised
4. On hotkey release, any remaining untranscribed audio is batch-transcribed and the remainder is typed
5. Full transcription is saved to the history log

No audio, text, or telemetry leaves the machine at any point.

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────┐
│                      MainWindow.xaml(.cs)               │
│  WPF UI · Settings · Tray Icon · Event Routing          │
└────────────┬───────────────────────┬────────────────────┘
             │                       │
             ▼                       ▼
┌────────────────────┐   ┌──────────────────────────────────────┐
│  GlobalKeyboardHook│   │          WhisperController            │
│  WH_KEYBOARD_LL    │   │                                      │
│  SetWindowsHookEx  │   │  ┌──────────────────────┐           │
│                    │   │  │  NAudio WasapiCapture  │           │
│  HotkeyStateChanged│   │  │  byte[] capture buffer │           │
│  (true/false)      │   │  └──────────┬───────────┘           │
└────────────────────┘   │             │ WAV bytes               │
                         │  ┌──────────▼───────────┐           │
                         │  │  WhisperNet / Whisper  │           │
                         │  │  iModel · Context      │           │
                         │  │  runFull(iAudioBuffer) │           │
                         │  └──────────┬───────────┘           │
                         │             │ transcription           │
                         │  ┌──────────▼───────────┐           │
                         │  │   FillerWordFilter     │           │
                         │  │   DictionaryService    │           │
                         │  └──────────┬───────────┘           │
                         └─────────────┼──────────────────────┘
                                       │
                         ┌─────────────▼──────────────┐
                         │  HistoryService              │
                         │  %AppData%\history.json      │
                         └─────────────────────────────┘
                                       │
                         ┌─────────────▼──────────────┐
                         │    KeyboardSimulator         │
                         │  SendInput KEYEVENTF_UNICODE │
                         └─────────────────────────────┘
```

**Thread model:**
- UI thread: WPF message pump, hook callback, state updates via `Dispatcher.Invoke`
- Background Task: `WhisperController.StopRecordingAsync` → `RunFullTranscription` runs on `Task.Run`
- Partial transcription loop: fires every 4 seconds on a background task; guarded by `SemaphoreSlim(1,1)` against the final transcription
- WASAPI callback thread: `DataAvailable` writes to `_captureBuffer` under lock

---

## 3. Current Features (v0.6.0)

### 3.1 Global Hotkey Capture

**File:** `GlobalKeyboardHook.cs`

- Installs a Windows low-level keyboard hook (`WH_KEYBOARD_LL`) via `SetWindowsHookEx` using the current process module handle and thread ID 0 (system-wide).
- Fires `HotkeyStateChanged(true)` on first `WM_KEYDOWN` / `WM_SYSKEYDOWN` for the configured virtual key code; suppresses Windows auto-repeat by tracking `_isKeyPressed`.
- Fires `HotkeyStateChanged(false)` on `WM_KEYUP` / `WM_SYSKEYUP`.
- Optionally swallows the hotkey event (returns `(IntPtr)1` instead of calling `CallNextHookEx`) to prevent side effects — enabled by default for Caps Lock and Left Alt.
- Default hotkey: **Caps Lock** (virtual key code `0x14`).
- Available hotkeys: Caps Lock (`0x14`), Scroll Lock (`0x91`), Left Alt (`0xA4`), Left Ctrl (`0xA2`), Ctrl+Win (`0x5B` + modifier `0x11`), F9 (`0x78`), F10 (`0x79`).
- Combo hotkeys specify a primary key and an optional modifier virtual key code (`ModifierVirtualCode`). The modifier is tracked generically — `VK_CONTROL (0x11)` matches both Left and Right Ctrl. Releasing either key in a combo fires the stop.

**Constraint:** The hook must be installed from a thread with a running message pump. In WPF this is the UI thread; the hook is installed in `MainWindow` constructor.

### 3.2 Audio Capture

**File:** `WhisperController.cs` — `StartRecording`, `StopWasapiAndGetWav`

- Uses **NAudio 2.2.1** `WasapiCapture` in shared mode.
- Device is selected by the Windows MMDevice endpoint ID string (obtained from `iMediaFoundation.listCaptureDevices()` via WhisperNet, matched to NAudio via `MMDeviceEnumerator.GetDevice(endpoint)`).
- Captured audio bytes are accumulated in a `List<byte>` under a lock from the `DataAvailable` callback. No silence detection or VAD — captures continuously while the hotkey is held.
- On `StopRecordingAsync`: capture stops, the raw bytes plus a WAV file header are written into a `MemoryStream` using `NAudio.Wave.WaveFileWriter`. The WAV is in the device's native format (typically 32-bit float, 48 kHz, stereo or mono).
- `GetWavSnapshot()` produces a WAV from the live buffer without stopping capture, used by the partial transcription loop.

**Why WASAPI instead of WhisperNet's `runCapture`:** WhisperNet's `runCapture` relies on VAD silence detection. USB audio devices with high noise floors never produce a detectable silence period, causing `onNewSegment` to never fire. WASAPI raw capture bypasses VAD entirely.

### 3.3 Whisper Transcription Engine

**File:** `WhisperController.cs` — `RunFullTranscription`, `LoadModelAsync`

**Model loading:**
- Uses `WhisperNet 1.12.0` (wraps Const-me's DirectCompute `Whisper.dll`).
- Attempts GPU load first via `Library.loadModelAsync(..., eModelImplementation.GPU)` with the user-selected adapter name.
- Falls back to CPU reference implementation (`eModelImplementation.Reference`) if GPU load fails.
- `Context` is created from the model and held for the lifetime of the loaded model.
- Context is configured with: selected language (`eLanguage`), translate flag off, CPU thread count = `ProcessorCount / 2`.

**Transcription:**
- `RunFullTranscription` writes the WAV byte array to a temporary file in `%TEMP%` (`wt_{guid}.wav`).
- Loads the temp file as `iAudioBuffer` via `iMediaFoundation.loadAudioFile(path, stereo: false)`. Media Foundation handles sample rate and format conversion internally (device-native → 16 kHz mono float32).
- Calls `context.runFull(buffer, callbacks)` — synchronous batch transcription.
- Result is read from `context.results(eResultFlags.None).segments`; falls back to text accumulated by `MyCallbacks.onNewSegment` if `results` is empty.
- Temp file is deleted in a `finally` block.

**Post-processing pipeline (applied after every transcription, partial and final):**
1. `FillerWordFilter.Apply` — removes filler words
2. `DictionaryService.Apply` — applies custom word replacements

**GPU support:** NVIDIA, AMD, and Intel GPUs via DirectCompute (Direct3D 11). The native `Whisper.dll` is bundled automatically by the WhisperNet NuGet package.

### 3.4 Sentence Streaming

**File:** `WhisperController.cs` — `RunPartialTranscriptionLoop`, `FindLastSentenceBoundary`, `StripTypedPrefix`

While the hotkey is held, a background loop fires every 4 seconds to transcribe the accumulated audio and stream complete sentences to the cursor, giving the user immediate feedback without waiting for the full recording to end.

**Algorithm:**
1. Every `PartialIntervalMs` (4000 ms), take a WAV snapshot of the live capture buffer.
2. Run full transcription on the snapshot and apply filler + dictionary filters.
3. Find the last sentence boundary (`.` `!` `?`) in the result — only type up to that point to avoid cutting mid-word during active speech.
4. Compute the new suffix since `_partialTypedText` (what was already typed in previous cycles).
5. Fire `PartialTranscriptionReady` with the new suffix only; update `_partialTypedText`.

**Deduplication on final transcription:**
- `StripTypedPrefix` compares the final transcription against `_partialTypedText` to find the already-typed prefix.
- Uses word-level fuzzy matching (strips trailing punctuation per word before comparing) to tolerate Whisper's non-determinism between partial and final runs — e.g. "remove." in a partial matching "remove" in the final.
- Only the untyped remainder is fired via `TranscriptionCompleted`.
- `_partialTypedText` is reset at the start of each new recording.

**Thread safety:** The partial loop and the final transcription both acquire `_transcriptionLock (SemaphoreSlim(1,1))` before calling into Whisper, preventing concurrent use of the shared `Context`.

### 3.5 Keyboard Output Injection

**File:** `KeyboardSimulator.cs`

- Uses Win32 `SendInput` with `KEYEVENTF_UNICODE` flag.
- For each character in the transcription string, generates a key-down + key-up `INPUT` struct with `wScan` set to the UTF-16 code unit. All inputs are submitted in a single `SendInput` call.
- Supports full Unicode including non-ASCII characters, punctuation, and multi-language scripts.
- Called for both partial (streaming) and final transcription output.

**Limitation:** Surrogate pairs (characters above U+FFFF) are not explicitly handled — each `char` in the C# string is sent as-is. Correct for the BMP range; may produce incorrect output for emoji or rare Unicode beyond U+FFFF.

### 3.6 Model Loading & Manager

**File:** `MainWindow.xaml.cs` — `ScanForModels`, `TriggerEagerModelLoad`; Model Manager panel

**Auto-scan directories:**
- `C:\Tools\whisper\models`
- `C:\Program Files\Audacity\openvino-models`
- `%AppData%\WhisperTyper\models\` (downloaded models)

**Model Manager panel:**
- Lists a hardcoded catalog of standard GGML models with size, accuracy tier, and status (`Not downloaded` / `Downloaded` / `Loaded`).
- **Download** button: async `HttpClient` download with progress bar; file saved to the configurable models directory. Cancellable via `CancellationTokenSource`.
- **Load** button: triggers `WhisperController.LoadModelAsync` for downloaded models.
- **Delete** button: removes `.bin` file from disk with confirmation (not available for currently loaded model).

**Catalog source:** `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{filename}`

| Model | Size | Notes |
|---|---|---|
| `ggml-tiny.bin` | 75 MB | Development/testing |
| `ggml-base.bin` | 142 MB | Good for general use |
| `ggml-small.bin` | 466 MB | Good balance |
| `ggml-medium.bin` | 1.5 GB | Very good accuracy |
| `ggml-large-v3.bin` | 3.1 GB | Excellent accuracy |
| `ggml-large-v3-turbo.bin` | 1.5 GB | **Recommended** |

### 3.7 History Log

**File:** `HistoryService.cs`, `MainWindow.xaml.cs`

Persistent searchable record of all past transcriptions.

**Data model per entry:** `Id` (Guid), `Timestamp` (DateTimeOffset), `Text` (string), `ModelName`, `DurationMs`, `Language`.

**Storage:** `%AppData%\WhisperTyper\history.json` — JSON array, newest first, max 500 entries (oldest pruned).

**UI (History panel):**
- Scrollable list of entries showing timestamp and text preview.
- Per-entry actions: **Copy to clipboard**, **Re-type at cursor** (`KeyboardSimulator.SimulateTypeString`), **Delete**.
- Search box filters entries by text content (client-side).
- "Clear all history" button with confirmation dialog.
- `HistoryList.ItemsSource` is always assigned a new list snapshot (`.ToList()`) on each refresh to avoid WPF ItemContainerGenerator desync with a live `List<T>`.

### 3.8 Custom Dictionary

**File:** `DictionaryService.cs`

Post-processing step that replaces words or phrases Whisper consistently mishears.

**Behaviour:**
- Each entry is a (trigger, replacement) pair; matching is case-insensitive at word boundaries (`\b` regex).
- Replacement preserves the original casing style (ALL CAPS → uppercased, Title Case → title-cased, otherwise as-is).
- Regex patterns compiled once on load; recompiled when the dictionary changes.

**Storage:** `%AppData%\WhisperTyper\dictionary.json` — JSON array of `{ "trigger", "replacement" }`.

**UI (Dictionary panel):** Editable data grid with Trigger and Replacement columns; Add / Delete row buttons; changes saved immediately.

### 3.9 Filler Word Removal

**File:** `FillerWordFilter.cs`

Strips common spoken filler words from transcription output before text is typed.

**Default list:** `um, uh, er, ah, like, you know, I mean, sort of, kind of, basically, literally, actually, so, well, right, okay, yeah`

**Behaviour:** Case-insensitive word-boundary matching; post-removal cleanup of double spaces and dangling punctuation. Master enable/disable toggle. Fully user-editable list with "Reset to defaults".

**Applied first** in the post-processing pipeline, before `DictionaryService`.

**Storage:** `FillerWords` (string array) and `FillerWordRemovalEnabled` (bool) in `settings.json`.

### 3.10 Settings Persistence

**File:** `MainWindow.xaml.cs` — `SaveSettings`, `LoadSettings`

**Format:** JSON, written to `%AppData%\WhisperTyper\settings.json`

**Persisted fields:**

| Field | Type | Description |
|---|---|---|
| `ModelPath` | string | Full path to the last loaded `.bin` model |
| `GpuAdapter` | string | Display name of the selected GPU adapter |
| `Language` | string | Display name of the selected language |
| `HotkeyIndex` | int | Index into the `HotkeyOption` list (0=Caps Lock … 6=F10) |
| `FillerWords` | string[] | Current filler word list |
| `FillerWordRemovalEnabled` | bool | Master toggle for filler word removal |
| `ModelsDirectory` | string | Directory for downloaded models |

Settings are loaded on `MainWindow.Loaded` and saved on `MainWindow.Closed`.

### 3.11 User Interface

**File:** `MainWindow.xaml`, `MainWindow.xaml.cs`

**Window:** Dark-themed WPF window (`#0E0B16` background), non-resizable. App icon shown in taskbar, title bar, and system tray (`App.ico` — 6-size ICO with 16/32/48/64/128/256 px BMP frames).

**Status indicator:** Filled ellipse with animated outer ring. Colors:
- Grey — model unloaded
- Orange — model loading
- Green (emerald) — ready, GPU mode
- Blue — ready, CPU mode
- Red + pulsing ring — recording
- Cyan — transcribing / typing

**Sub-status line:** Shows partial transcription preview (`🎙 "..."`) while recording.

**Panels (tab-switched):**

| Panel | Contents |
|---|---|
| Main | Model path, GPU, microphone, language, hotkey selectors; status indicator |
| History | Scrollable transcription history with search, copy, re-type, delete |
| Dictionary | Editable trigger/replacement grid |
| Filler Words | Toggle + chip list of filler words |
| Models | Model catalog with download/load/delete actions |
| Diagnostics | Append-only timestamped log of all internal events |

**System tray:**
- Icon shown at all times via `System.Windows.Forms.NotifyIcon`.
- Double-click or "Open Settings" restores window; "Exit" closes the app.
- Balloon tip shown on minimize.

---

## 4. Planned Features

No features currently planned. Candidates for future work:

| Area | Idea |
|---|---|
| Unicode | Handle surrogate pairs in `KeyboardSimulator` for emoji / beyond U+FFFF |
| Installer | MSI or MSIX package for one-click install |
| Max recording | Enforce a configurable maximum recording duration with a warning |
| Multi-model | Preload a second model while one is active for instant switching |

---

## 5. Data & File Locations

| File | Path | Contents |
|---|---|---|
| Settings | `%AppData%\WhisperTyper\settings.json` | User preferences |
| History | `%AppData%\WhisperTyper\history.json` | Transcription history entries |
| Dictionary | `%AppData%\WhisperTyper\dictionary.json` | Custom word replacements |
| Downloaded models | `%AppData%\WhisperTyper\models\` | GGML `.bin` files |
| Temp audio | `%TEMP%\wt_{guid}.wav` | Per-transcription temp file, deleted immediately after use |

---

## 6. Dependencies

| Package | Version | Purpose |
|---|---|---|
| `WhisperNet` | 1.12.0 | Whisper inference (wraps Const-me's native `Whisper.dll` via DirectCompute) |
| `NAudio` | 2.2.1 | WASAPI audio capture, WAV file writing |
| .NET WPF | 10.0-windows | UI framework |
| .NET Windows Forms | 10.0-windows | `NotifyIcon` for system tray |

**Native dependency (auto-downloaded by NuGet):**
- `Whisper.dll` — Const-me's DirectCompute implementation of Whisper, x64 Windows only.

---

## 7. Known Limitations

| Area | Limitation |
|---|---|
| Platform | Windows x64 only. The native `Whisper.dll` has no ARM or cross-platform build. |
| Unicode | Characters above U+FFFF (emoji, some CJK extensions) may not inject correctly via `SendInput` surrogate pairs. |
| Max recording | No enforced maximum recording duration. Very long recordings (>60 s) will produce large temp WAV files and may exhaust GPU VRAM during transcription. |
| Model memory | Loading large models (large-v3, 3.1 GB) requires sufficient GPU VRAM; no VRAM check before load attempt. |
| Multi-model | Only one model can be loaded at a time. Switching models requires unloading the current one first. |
| Focus timing | Text is typed at the window focused at the moment `SendInput` is called. If the user switches focus during transcription, text goes to the wrong window. |
| Streaming accuracy | Partial transcriptions use a separate Whisper run on the same context; non-determinism between runs means the streamed and final text can differ slightly. Word-level fuzzy deduplication handles most cases but is not guaranteed to be perfect. |
| No installer | Distributed as source only; no MSI or MSIX installer package. |
