# Changelog

## v0.7.0 — 2026-06-10

### New
- **App Profiles (Auto-switching)** — Automatically change language, translation, and custom rules based on the focused window. Includes a "Clone Current Settings" feature with a 3-second countdown to easily create profiles for any application.
- **LLM Post-Processing** — Connect to local LLMs via **Ollama** or **LM Studio**. Refine, translate, or reformat your transcription using custom prompts (e.g., "Translate to German" or "Fix grammar") before it's typed.
- **Default Profile** — A permanent "Default (All Other Apps)" profile to manage global settings using the same intuitive Load/Save workflow as app profiles.

### Fixed
- **"Unknown language ''" crash** — Fixed a regression where selecting "Auto-Detect" language would crash the Whisper engine.
- **Transcription Truncation** — Resolved an issue where long speech segments were truncated when using LLM post-processing.
- **Ambiguous UI references** — Fixed compilation errors related to ambiguous class names in the codebase.
- **Sorted Language List** — The target language dropdown is now sorted alphabetically for easier navigation.

---

## v0.6.1 — 2026-06-05

### Fixed
- Filler Word Removal "Enabled" checkbox clipped by scrollbar — header grid used a hardcoded `Width="660"`; replaced with `HorizontalContentAlignment="Stretch"` so it adapts to available width
- Release notes now pulled from `CHANGELOG.md` instead of auto-generated commit titles

---

## v0.6.0 — 2026-06-05

### New
- **Sentence streaming** — complete sentences are typed at the cursor every ~4 seconds mid-recording; no need to wait until hotkey release for long dictations
- **App icon** — custom icon shown in taskbar, title bar, and system tray

### Fixed
- Duplicate text when streaming: word-level fuzzy deduplication now handles Whisper punctuation differences between partial and final transcription runs (e.g. "remove." vs "remove")
- `App.ico` crash on startup — original file had a corrupted PNG-compressed frame; rebuilt with clean BMP frames compatible with WPF's image decoder
- History panel crash — `HistoryList` was bound to the live `List<T>`; WPF's ItemContainerGenerator desynced when the list was mutated off-thread; fixed by assigning a snapshot (`.ToList()`) on each refresh
- Filler Word Removal "Enabled" checkbox clipped by scrollbar — header grid used a hardcoded `Width="660"` instead of stretching to available width

---

## v0.5.0 — 2026-06-04

### New
- **History log** — persistent searchable record of transcriptions with copy, re-type, and delete per entry
- **Model Manager** — browse, download, and switch GGML models from within the app
- **Custom Dictionary** — define word replacements for terms Whisper consistently mishears
- **Filler Word Removal** — automatically strip "um", "uh", "like", etc. before typing; fully configurable

---

## v0.4.0

- Initial public release with hold-to-record, GPU transcription, and cursor injection
