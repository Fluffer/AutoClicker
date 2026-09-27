# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-09-27

A large release that turns AutoClicker from a blind repeater into a scriptable,
reactive automation engine, and lets AI assistants drive it over MCP.

### Added

- **Control flow** — `repeat` blocks, `if`/`else` conditionals, variables
  (`set var`) and `goto`/labels, so sequences behave like small programs
  (`FormatVersion 2` in `.acseq` files).
- **Visual targeting** — find a point by image-template match or by OCR text
  (`Windows.Media.Ocr`), so a sequence can locate UI that has moved
  (`FormatVersion 3`).
- **Real recording with timing** — *Record* now captures the actual pauses
  between clicks, not a single fixed interval.
- **Scheduler** — named schedules (a profile or a sequence file) with a watcher
  that fires them while the app is open.
- **Run logging** — an optional per-step JSONL audit trail for every run.
- **MCP server** — `AutoClicker.exe --mcp` exposes the engine to AI assistants
  over JSON-RPC 2.0, on a named pipe or `--stdio`. Tools: `list_profiles`,
  `list_sequences`, `read_sequence`, `create_sequence`, `run_sequence`,
  `stop_run`, `get_state`.
- **Self-healing playback** — a recorded step can optionally re-target its
  control through a captured UI Automation selector, falling back to the exact
  recorded coordinates on any miss.
- **Dark mode** — light (classic), dark, or follow-system colour mode via the
  .NET `SystemColorMode` API; chosen at startup.
- **Data migration** — settings, profiles, schedules and logs moved from
  `%AppData%\AutoClicker` to `Documents\AutoClicker` so an MSIX uninstall no
  longer deletes them.
- `CHANGELOG.md` (this file) and a release workflow (`.github/workflows/release.yml`).

### Changed

- Refactored the single `Form1` "God form" into focused controllers (hotkeys,
  recording, profiles, run orchestration).
- Version is now single-sourced from `AutoClicker.csproj`; `Build-Package.ps1`
  reads it and injects `major.minor.patch.0` into the MSIX manifest before packing.
- Reliability pass: humanized input, rebindable panic key, corner fail-safe, a
  run watchdog, and a backend ladder (real input → posted messages → UI Automation).
- `Build-Package.ps1` now fails fast when the SDK tools are missing or the
  certificate subject doesn't match the manifest publisher, and takes the signing
  thumbprint from a parameter or `$env:SIGN_THUMBPRINT` instead of a hardcoded value.

### Fixed

- A batch of critical bugs found during hardening (C5, C6, H2, H5, H6, H7, H8, M15).

### Security

- The MCP named pipe's ACL grants access to the current Windows user only.

## [1.1.0] - 2026-08-02

### Added

- Pixel-colour conditions — gate an action on a screen pixel, or wait for one.
- Profiles (named, switchable sequences, each with an optional hotkey).
- System tray icon.
- Command-line (headless) mode.
- UI Automation click backend for WPF/UWP/Chrome.
- Drags, scrolling, keystrokes, window anchoring and humanized input.
- Settings persistence, an Esc panic stop, and a start delay.

### Fixed

- Engine races, a stuck button on close, and interval accuracy.
- A stuck background drag, profile data loss, and pixel-picking the hover colour.

## [1.0.0] - 2026-06-29

### Added

- Initial release: click/hold at a point, multi-point sequences, and MSIX packaging.

## Known limitations

- DirectInput/raw-input games can only be reached with real input (`SendInput`).
- The colour mode is read once at startup; changing it needs a relaunch.
- MCP schedule management and UIA `GetClickablePoint` fallback are deferred.
- The screenshot in `README.md` shows the v1.x layout.
