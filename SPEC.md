# WhisperTyper — Product Specification

**Repository:** https://github.com/joywareapps/WhisperTyper  
**Platform:** Windows 10/11 x64  
**Framework:** .NET 10, WPF  
**License:** MIT

---

## Table of Contents

1. [Overview](#1-overview)
2. [Architecture](#2-architecture)
3. [Current Features (v1.0)](#3-current-features-v10)
   - 3.1 [Global Hotkey Capture](#31-global-hotkey-capture)
   - 3.2 [Audio Capture](#32-audio-capture)
   - 3.3 [Whisper Transcription Engine](#33-whisper-transcription-engine)
   - 3.4 [Keyboard Output Injection](#34-keyboard-output-injection)
   - 3.5 [Model Loading](#35-model-loading)
   - 3.6 [Settings Persistence](#36-settings-persistence)
   - 3.7 [User Interface](#37-user-interface)
4. [Planned Features](#4-planned-features)
   - 4.1 [History Log](#41-history-log)
   - 4.2 [Model Manager](#42-model-manager)
   - 4.3 [Custom Dictionary](#43-custom-dictionary)
   - 4.4 [Filler Word Removal](#44-filler-word-removal)
5. [Data & File Locations](#5-data--file-locations)
6. [Dependencies](#6-dependencies)
7. [Known Limitations](#7-known-limitations)

---

## 1. Overview

WhisperTyper is a Windows system-tray application that converts speech to text using a locally-running Whisper model and types the result directly at the active cursor position in any application.

**Core user flow:**
1. User holds a configurable hotkey
2. Application records audio via WASAPI microphone capture
3. On hotkey release, audio is batch-transcribed by Whisper running on GPU (or CPU fallback)
4. Transcribed text is injected at the active cursor using `SendInput`

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
┌────────────────────┐   ┌──────────────────────────────┐
│  GlobalKeyboardHook│   │       WhisperController       │
│  WH_KEYBOARD_LL    │   │                              │
│  SetWindowsHookEx  │   │  ┌──────────────────────┐   │
│                    │   │  │  NAudio WasapiCapture  │   │
│  HotkeyStateChanged│   │  │  byte[] capture buffer │   │
│  (true/false)      │   │  └──────────┬───────────┘   │
└────────────────────┘   │             │ WAV bytes       │
                         │  ┌──────────▼───────────┐   │
                         │  │  WhisperNet / Whisper  │   │
                         │  │  iModel · Context      │   │
                         │  │  runFull(iAudioBuffer) │   │
                         │  └──────────┬───────────┘   │
                         │             │ transcription   │
                         └─────────────┼────────────────┘
                                       │
                                       ▼
                         ┌─────────────────────────┐
                         │    KeyboardSimulator      │
                         │  SendInput KEYEVENTF_    │
                         │  UNICODE per character    │
                         └─────────────────────────┘
```

**Thread model:**
- UI thread: WPF message pump, hook callback, state updates via `Dispatcher.Invoke`
- Background Task: `WhisperController.StopRecordingAsync` → `RunFullTranscription` runs on `Task.Run` to avoid blocking UI during transcription
- WASAPI callback thread: `DataAvailable` writes to `_captureBuffer` under lock

---

## 3. Current Features (v1.0)

### 3.1 Global Hotkey Capture

**File:** `GlobalKeyboardHook.cs`

- Installs a Windows low-level keyboard hook (`WH_KEYBOARD_LL`) via `SetWindowsHookEx` using the current process module handle and thread ID 0 (system-wide).
- Fires `HotkeyStateChanged(true)` on first `WM_KEYDOWN` / `WM_SYSKEYDOWN` for the configured virtual key code; suppresses Windows auto-repeat by tracking `_isKeyPressed`.
- Fires `HotkeyStateChanged(false)` on `WM_KEYUP` / `WM_SYSKEYUP`.
- Optionally swallows the hotkey event (returns `(IntPtr)1` instead of calling `CallNextHookEx`) to prevent side effects — enabled by default for Caps Lock and Left Alt.
- Default hotkey: **Caps Lock** (virtual key code `0x14`).
- Available hotkeys: Caps Lock (`0x14`), Scroll Lock (`0x91`), Left Alt (`0xA4`), Left Ctrl (`0xA2`), Ctrl+Win (`0x5B` + modifier `0x11`), F9 (`0x78`), F10 (`0x79`).
- Combo hotkeys specify a primary key and an optional modifier virtual key code (`ModifierVirtualCode`). The modifier is tracked generically — `VK_CONTROL (0x11)` matches both Left and Right Ctrl; same pattern applies to Shift (`0x10`) and Alt (`0x12`). Releasing either key in a combo fires the stop.

**Constraint:** The hook must be installed from a thread with a running message pump. In WPF this is the UI thread; the hook is installed in `MainWindow` constructor.

### 3.2 Audio Capture

**File:** `WhisperController.cs` — `StartRecording`, `StopWasapiAndGetWav`

- Uses **NAudio 2.2.1** `WasapiCapture` in shared mode.
- Device is selected by the Windows MMDevice endpoint ID string (obtained from `iMediaFoundation.listCaptureDevices()` via WhisperNet, matched to NAudio via `MMDeviceEnumerator.GetDevice(endpoint)`).
- Captured audio bytes are accumulated in a `List<byte>` under a lock from the `DataAvailable` callback. No silence detection or VAD — captures continuously while the hotkey is held.
- On `StopRecordingAsync`: capture stops, the raw bytes plus a WAV file header are written into a `MemoryStream` using `NAudio.Wave.WaveFileWriter`. The WAV is in the device's native format (typically 32-bit float, 48 kHz, stereo or mono depending on device).
- The WAV bytes are then passed to `RunFullTranscription`.

**Why WASAPI instead of WhisperNet's `runCapture`:** WhisperNet's `runCapture` relies on VAD silence detection to trigger transcription. USB audio devices with high noise floors never produce a detectable silence period, causing `onNewSegment` to never fire. WASAPI raw capture bypasses VAD entirely.

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
- Loads the temp file as `iAudioBuffer` via `iMediaFoundation.loadAudioFile(path, stereo: false)`. Media Foundation handles sample rate and format conversion internally (device-native → 16 kHz mono float32 that Whisper requires).
- Calls `context.runFull(buffer, callbacks)` — synchronous batch transcription.
- Result is read from `context.results(eResultFlags.None).segments` after `runFull` returns; falls back to text accumulated by `MyCallbacks.onNewSegment` if `results` is empty.
- Temp file is deleted in a `finally` block.

**GPU support:** NVIDIA, AMD, and Intel GPUs via DirectCompute (Direct3D 11). The native `Whisper.dll` is bundled automatically by the WhisperNet NuGet package.

**Supported models:** Any GGML `.bin` model compatible with Whisper.cpp / Const-me's implementation (base, small, medium, large-v2, large-v3, large-v3-turbo).

### 3.4 Keyboard Output Injection

**File:** `KeyboardSimulator.cs`

- Uses Win32 `SendInput` with `KEYEVENTF_UNICODE` flag.
- For each character in the transcription string, generates a key-down + key-up `INPUT` struct with `wScan` set to the UTF-16 code unit. All inputs are submitted in a single `SendInput` call.
- Supports full Unicode including non-ASCII characters, punctuation, and multi-language scripts.
- Output goes to whatever window had focus at the moment of the `SendInput` call (the focused window at key release time, since transcription runs asynchronously on a background task before typing begins).

**Limitation:** Surrogate pairs (characters above U+FFFF) are not explicitly handled — each `char` in the C# string is sent as-is. This is correct for the BMP range but may produce incorrect output for emoji or rare Unicode characters beyond U+FFFF.

### 3.5 Model Loading

**File:** `MainWindow.xaml.cs` — `ScanForModels`, `TriggerEagerModelLoad`

- On startup, scans these directories for `*ggml*.bin` files:
  - `C:\Tools\whisper\models`
  - `C:\Program Files\Audacity\openvino-models`
- Found models are populated into `ComboModelPath` (editable combo box).
- User can also browse for any `.bin` file via a file open dialog.
- Model is loaded automatically when a new item is selected in `ComboModelPath` (`SelectionChanged` event), or when the Reload button is clicked.
- Model load triggers `WhisperController.LoadModelAsync` on a background `Task.Run`.

### 3.6 Settings Persistence

**File:** `MainWindow.xaml.cs` — `SaveSettings`, `LoadSettings`

**Format:** JSON, written to `%AppData%\WhisperTyper\settings.json`

**Persisted fields:**

| Field | Type | Description |
|---|---|---|
| `ModelPath` | string | Full path to the last loaded `.bin` model |
| `GpuAdapter` | string | Display name of the selected GPU adapter |
| `Language` | string | Display name of the selected language (e.g. `"English"`, `"Auto-Detect"`) |
| `HotkeyIndex` | int | Index into the hardcoded `HotkeyOption` list in `ComboHotkey` (0=Caps Lock, 1=Scroll Lock, 2=Left Alt, 3=Left Ctrl, 4=Ctrl+Win, 5=F9, 6=F10) |

Settings are loaded on `MainWindow.Loaded` and saved on `MainWindow.Closed`.

### 3.7 User Interface

**File:** `MainWindow.xaml`, `MainWindow.xaml.cs`

**Window:** Dark-themed WPF window, non-resizable.

**Status indicator:** Filled ellipse with animated outer ring. Colors:
- Grey — model unloaded
- Orange — model loading
- Green (emerald) — ready, GPU mode
- Blue — ready, CPU mode
- Red + pulsing ring — recording
- Cyan — transcribing

**Controls:**

| Control | Behavior |
|---|---|
| `ComboModelPath` | Editable combo, auto-populated from scan, triggers model load on selection change |
| Browse button | Opens `OpenFileDialog` filtered to `*.bin`, adds to combo and loads |
| Reload button | Re-triggers model load with current combo text |
| `ComboGpu` | Lists DirectCompute adapters from `Library.listGraphicAdapters()` |
| `ComboLanguage` | Lists all `eLanguage` enum values plus "Auto-Detect" (index 0 = `(eLanguage)0`) |
| `ComboHotkey` | Hardcoded list of 6 hotkey options |
| `TxtHistory` | Append-only `TextBox` showing timestamped log entries |
| Clear History button | Clears `TxtHistory` |
| Minimize button | Hides window, shows tray balloon |

**System tray:**
- Icon shown at all times via `System.Windows.Forms.NotifyIcon`.
- Double-click or "Open Settings" context menu item restores window.
- "Exit" context menu item closes the application.
- Balloon tip shown on minimize: "The app is running in the background."

**Diagnostic log:** All events (hotkey press/release, capture start, transcription progress, results) are appended to `TxtHistory` with `HH:mm:ss` timestamps.

---

## 4. Planned Features

---

### 4.1 History Log

**Purpose:** Persistent searchable record of all past transcriptions, allowing users to review, copy, or re-type previous results.

**Scope:** Session history visible within the app, persisted to disk across restarts.

#### Data Model

Each history entry stores:

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | Unique identifier |
| `Timestamp` | `DateTimeOffset` | UTC time of transcription completion |
| `Text` | `string` | Raw transcription text (after any post-processing) |
| `ModelName` | `string` | Filename of the model used (e.g. `ggml-large-v3-turbo.bin`) |
| `DurationMs` | `int` | Audio recording duration in milliseconds |
| `Language` | `string` | Language setting at time of recording |

#### Storage

- File: `%AppData%\WhisperTyper\history.json`
- Format: JSON array of history entries, newest first.
- Maximum entries: 500 (oldest entries pruned when limit is exceeded).
- Entries are appended on each successful transcription; file is rewritten in full on each save.

#### UI

- New tab or collapsible panel in `MainWindow`: **History**
- Displays a scrollable list of entries, each showing: timestamp, first 80 characters of text, model name.
- Clicking an entry shows the full text in a detail view.
- Per-entry actions: **Copy to clipboard**, **Re-type at cursor** (re-runs `KeyboardSimulator.SimulateTypeString`), **Delete**.
- Search box: filters displayed entries by text content (client-side, no indexing required at this scale).
- "Clear all history" button with confirmation dialog.

#### Implementation Notes

- History service is a standalone `HistoryService` class, injected into `WhisperController` or wired via event in `MainWindow`.
- `WhisperController.TranscriptionCompleted` event carries the text; `MainWindow` calls `HistoryService.Add(entry)` and triggers UI refresh.
- No database dependency — JSON file is sufficient for ≤500 entries.

---

### 4.2 Model Manager

**Purpose:** Allow users to discover, download, and switch between GGML models from within the app, without manually navigating Hugging Face or file systems.

**Scope:** A dedicated panel listing known models with their size, accuracy tier, and download/load status.

#### Known Model Catalog

The app ships with a hardcoded catalog of the standard Whisper GGML model variants:

| Model | Size | Speed | Accuracy | Notes |
|---|---|---|---|---|
| `ggml-tiny.bin` | 75 MB | Very fast | Low | Development/testing |
| `ggml-tiny.en.bin` | 75 MB | Very fast | Low | English-only |
| `ggml-base.bin` | 142 MB | Fast | Moderate | Good for general use |
| `ggml-base.en.bin` | 142 MB | Fast | Moderate | English-only |
| `ggml-small.bin` | 466 MB | Moderate | Good | |
| `ggml-small.en.bin` | 466 MB | Moderate | Good | English-only |
| `ggml-medium.bin` | 1.5 GB | Slow | Very good | |
| `ggml-large-v3.bin` | 3.1 GB | Very slow | Excellent | |
| `ggml-large-v3-turbo.bin` | 1.5 GB | Moderate | Excellent | **Recommended** |

Download source: `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{filename}`

#### UI

- New tab or panel: **Models**
- Table/list showing each catalog entry with columns: Name, Size, Status (`Not downloaded` / `Downloaded` / `Loaded`), Action button.
- **Download** button: starts async download with a progress bar; file saved to a configurable models directory (default: `%AppData%\WhisperTyper\models\`).
- **Load** button: available when file exists; triggers `WhisperController.LoadModelAsync`.
- **Delete** button: removes the `.bin` file from disk (with confirmation) when not currently loaded.
- Local model directory is also scanned on startup; any `.bin` files found are added to the combo and shown in the manager with "Downloaded" status.

#### Implementation Notes

- Download via `HttpClient` with `IProgress<long>` for byte-level progress.
- Checksum verification (SHA256) against a hardcoded manifest to confirm file integrity after download.
- Cancellable downloads (CancellationTokenSource exposed to the UI).
- Model directory path is user-configurable and persisted in `settings.json`.

#### Settings additions

| Field | Type | Description |
|---|---|---|
| `ModelsDirectory` | `string` | Directory where downloaded models are stored |

---

### 4.3 Custom Dictionary

**Purpose:** Allow users to define words or phrases that Whisper consistently mishears and replace them with the correct spelling on each transcription.

**Scope:** A simple find-and-replace list applied as a post-processing step after transcription, before text is typed at the cursor.

#### Behaviour

- Each dictionary entry is a (trigger, replacement) pair.
- Matching is case-insensitive on the trigger.
- Replacement preserves the original casing style:
  - If the matched text is ALL CAPS → replacement is uppercased.
  - If the matched text is Title Case → replacement is title-cased.
  - Otherwise → replacement is used as-is.
- Matching uses whole-word boundaries (regex `\b`) to avoid partial replacements (e.g. "his" should not match inside "this").
- Entries are applied in order; a word already replaced is not re-matched.

**Example entries:**

| Trigger (Whisper output) | Replacement |
|---|---|
| `wisper` | `Whisper` |
| `eye phone` | `iPhone` |
| `chat gpt` | `ChatGPT` |
| `john smith` | `John Smith` |

#### Storage

- File: `%AppData%\WhisperTyper\dictionary.json`
- Format: JSON array of `{ "trigger": "...", "replacement": "..." }` objects.

#### UI

- New tab or panel: **Dictionary**
- Editable data grid with two columns: Trigger, Replacement.
- Add row button, delete selected row button.
- Changes are saved immediately (on each edit) or via explicit Save button — TBD.
- Import/export as CSV for easy sharing.

#### Implementation Notes

- `DictionaryService` class with a `Apply(string text) → string` method.
- Called inside `WhisperController.StopRecordingAsync` after `RunFullTranscription` returns, before `TranscriptionCompleted` is fired.
- Regex patterns are compiled once when the dictionary is loaded and cached; recompiled when dictionary changes.

---

### 4.4 Filler Word Removal

**Purpose:** Automatically strip common spoken filler words from transcription output so the typed text reads cleanly without manual editing.

**Scope:** A configurable post-processing step applied after transcription and before custom dictionary replacement.

#### Default Filler Word List

```
um, uh, er, ah, like, you know, I mean, sort of, kind of,
basically, literally, actually, so, well, right, okay, yeah
```

#### Behaviour

- Each filler word/phrase is matched case-insensitively at word boundaries.
- After removal, double spaces, leading/trailing spaces, and dangling punctuation (e.g. ", , word") are cleaned up.
- The list is fully user-editable — users can add domain-specific fillers or remove entries they want to keep.
- A master enable/disable toggle turns the entire feature on or off without clearing the list.

**Example:**

| Input | Output |
|---|---|
| `"Um, I think, like, we should uh go to the meeting"` | `"I think we should go to the meeting"` |
| `"So basically what I'm saying is, you know, it works"` | `"What I'm saying is it works"` |

#### Storage

- Filler words list stored in `settings.json` under a `FillerWords` string array field.
- Enable/disable toggle stored as `FillerWordRemovalEnabled` boolean in `settings.json`.

#### UI

- Settings panel section: **Filler Word Removal**
- Toggle switch: Enable / Disable
- Editable tag list (chips) of filler words — click a chip to remove, text field + Add button to add new ones
- "Reset to defaults" button

#### Implementation Notes

- `FillerWordFilter` class with `Apply(string text) → string` method.
- Called in `WhisperController.StopRecordingAsync` as the first post-processing step (before custom dictionary).
- Regex patterns compiled once on filter construction; recompiled when the word list changes.
- Processing order in pipeline: `RunFullTranscription` → `FillerWordFilter.Apply` → `DictionaryService.Apply` → `TranscriptionCompleted` event → `KeyboardSimulator.SimulateTypeString`.

---

## 5. Data & File Locations

| File | Path | Contents |
|---|---|---|
| Settings | `%AppData%\WhisperTyper\settings.json` | User preferences |
| History | `%AppData%\WhisperTyper\history.json` | Transcription history entries |
| Dictionary | `%AppData%\WhisperTyper\dictionary.json` | Custom word replacements |
| Downloaded models | `%AppData%\WhisperTyper\models\` | GGML `.bin` files (default location) |
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
| Focus timing | Text is typed at the window that was focused at the moment `SendInput` is called, which is after transcription completes. If the user switches focus during transcription, text goes to the wrong window. |
| No installer | Distributed as source only; no MSI or MSIX installer package yet. |
