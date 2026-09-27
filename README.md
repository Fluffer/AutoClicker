# Auto Clicker

A lightweight Windows mouse auto-clicker (C# / WinForms, .NET 10). Click or hold at a
fixed point, or record and run **sequences** of clicks, drags, scrolls, keystrokes,
control-flow blocks and visual lookups.

![Auto Clicker](docs/screenshot.png)

> **Note:** the screenshot shows the v1.x layout — the current UI has a few more
> options (control flow, visual targeting, colour mode, scheduling).

## Features

- **Click / Double-click / Hold** with any mouse button (left / right / middle)
- Adjustable interval (hours / mins / secs / ms), accurate to ~1 ms
- Repeat **N times** or **until stopped**
- **Single point** (current cursor or a picked X/Y) **or multi-action sequences**
- Sequences aren't clicks-only — each step can be a **click, drag, scroll,
  key combo, typed text, plain wait, pixel wait, control-flow block, or a visual
  lookup**
- **Control flow** — `repeat` loops, `if`/`else` conditionals, variables and
  `goto`/labels, so a sequence can make decisions instead of blindly repeating
- **Visual targeting** — locate a point by matching an on-screen image template
  or by OCR text, and act on where it was found
- **Record by clicking** — hit *Record*, click each target, right-click or **F8** to
  finish; the real pauses between your clicks are captured as per-step waits
- Per-step **edit** and reorder
- **Window anchoring** — anchor a point to the window it was recorded in and it's
  stored as client coordinates, so the sequence survives that window being moved,
  resized or reopened
- **Humanize** — randomise position by ±N px and timing by ±N %, so the input
  isn't metronomically identical
- **Save / load** sequences as `.acseq` (JSON) for reuse. Files written by earlier
  versions still load.
- **Background mode** (experimental) — posts input to the window under each point
  without moving your real cursor, so you can keep working on another monitor
  *(target-dependent — see notes below)*
- **Schedules** — run a profile or sequence on a schedule; the app fires them while
  it's open
- **Run logging** — optional per-step JSONL audit trail for each run
- **MCP server** — let an AI assistant (Claude Desktop, VS Code Copilot, …) list,
  read, create and run sequences, see below
- Global **F6** start/stop hotkey (rebindable F1–F12); tells you if the key is
  already claimed by another app
- **Esc panic stop** — aborts the run and force-releases every mouse button, in case
  a hold or drag left one down. Esc is only claimed while a run is active, so it
  stays usable everywhere else.
- **Start delay** — count down N seconds before the run begins, so you can focus the
  target window first
- **Colour mode** — light (classic), dark, or follow the system theme (applies on
  next launch)
- **Settings persist** between launches in `Documents\AutoClicker\settings.json`,
  including the last sequence you saved or loaded
- **Profiles** — several named sequences, each with its own optional F1–F12 hotkey,
  so one key runs your farming loop and another runs your login macro
- **System tray** — optionally minimise to the tray and start/stop from there
- **Command line** — scriptable and schedulable, see below
- **Three ways to click** — real input, posted messages, or a UI Automation invoke
  that reaches WPF/UWP/Chrome without moving the cursor
- High-DPI aware, negative (multi-monitor) coordinates supported, custom icon,
  single self-contained build

## Action types

| Action | What it does |
|---|---|
| **Click** | Press a button at a point. Single or double, with an optional hold time. |
| **Drag** | Press at one point, travel to another over a set duration, release. |
| **Scroll** | Wheel notches at a point, vertical or horizontal. |
| **Key press** | A combo such as `Ctrl+C`, `Alt+Tab`, `Shift+F5`, `Enter`, `Esc`. |
| **Type text** | Literal text, sent as Unicode — the keyboard layout doesn't matter. |
| **Wait** | Nothing but the delay. Useful for letting a target catch up. |
| **Wait for pixel** | Block until a screen pixel matches (or stops matching) a colour, with an optional timeout. |
| **Repeat block** | Run the actions inside N times. |
| **If / else** | Run a branch only when a condition holds (pixel colour, variable, expression). |
| **Set variable** | Assign or increment a variable used by later conditions. |
| **Label / goto** | Jump to a labelled step. |
| **Find image** | Locate a screen region that matches a saved template image. |
| **Find text** | Locate text on screen with OCR, then act on its position. |

## Pixel conditions

Any action can be gated on the colour of a screen pixel, which is what turns a blind
repeater into something reactive:

- **Gate** — "only click if the pixel at (400,300) is `#1E90FF`", or *only if it isn't*.
  A gated-off action is skipped but still honours its wait, so a mostly-skipped
  sequence doesn't spin the CPU. Skipped rows are tinted grey-blue in the list.
- **Wait for pixel** — block until the pixel matches, then carry on. Optionally give up
  after N ms, and optionally stop the whole run when it does.

Pick the point and its colour together with **Pick pixel (3s)** — hover the thing you
care about and both the coordinates and the colour are captured. **Tolerance** is the
maximum per-channel difference that still counts as a match (0 = exact).

Pixel coordinates are always screen coordinates and are never window-anchored.

## Control flow

Beyond the flat list, sequences can carry structure:

- **Repeat** a block a set number of times (or until stopped).
- **If / else** branches gated on a pixel colour, a variable, or an expression such
  as `{attempts} < 3`.
- **Variables** (`set var`) and **labels / goto** for loops and early exits.

These are stored in the same `.acseq` file and run through the exact same engine as
everything else.

## Visual targeting

Instead of a fixed coordinate, a step can search the screen first:

- **Find image** matches a saved template against a screen region (with a tolerance)
  and acts at the best match.
- **Find text** runs Windows OCR on a region and acts where the text was found.

Together these let a sequence cope with windows that move between runs.

## Install

Download `AutoClicker.msix` from the [latest release](../../releases/latest) and
double-click it. The package is signed with a trusted CA code-signing certificate,
so there's no trust prompt or SmartScreen block.

Or via PowerShell:

```powershell
Add-AppxPackage -Path AutoClicker.msix
```

## Clicking without moving the cursor

Each click / drag / scroll action picks one of three methods:

| Method | Moves cursor | Reaches |
|---|---|---|
| **Real input** (`SendInput`) | Yes | Everything, including games |
| **Background messages** (`PostMessage`) | No | Classic Win32 windows |
| **UI Automation invoke** | No | WPF, UWP/WinUI, Chrome — most modern apps |

**Background messages** fail on Chrome/Electron (events arrive `isTrusted: false`),
on UWP/WinUI/WPF (one window handle, internal hit-testing), on DirectInput/raw-input
games, and on elevated windows (blocked by UIPI).

**UI Automation** covers most of that gap: it activates the control under the point
through its accessibility provider rather than faking input. Caveats worth knowing:

- It *invokes a control*, so it ignores the button choice, double-click and hold time.
- There is no UIA equivalent of a drag or a scroll notch — those fall back to
  background messages.
- It refuses to act when the point isn't over a window, rather than silently
  "succeeding" against the desktop.
- Nothing reaches DirectInput games except real input.

Keyboard actions in background mode are posted to the window of the most recent
positioned action, and are the least reliable part — synthesised `WM_KEY*` messages
are ignored by most modern frameworks.

## Safety

- **Panic stop** — Esc (rebindable to F1–F12) aborts the current run immediately and
  releases every held mouse button.
- **Corner fail-safe** — parking the cursor in a screen corner stops the run
  (classic PyAutoGUI-style escape hatch), optional.
- **Watchdog** — a maximum run length in seconds; 0 means unlimited.
- **Clean Ctrl+C** — a CLI run stopped with Ctrl+C always releases held buttons.

## Data location

Settings, profiles, schedules and run logs live under `Documents\AutoClicker`
(`%USERPROFILE%\Documents\AutoClicker`), **not** `%AppData%`. An MSIX full-trust
package virtualizes `%AppData%` into the package and deletes it on uninstall, so the
data is kept in Documents to survive uninstalls. The app migrates any legacy
`%AppData%\AutoClicker` data automatically on first launch.

| File | Contents |
|---|---|
| `settings.json` | UI state and preferences |
| `profiles.json` | Named profiles and their hotkeys |
| `schedules.json` | Schedules |
| `logs/` | Per-step JSONL run logs (when enabled) |
| `*.acseq` | Your saved sequences (wherever you save them) |

## Command line

With no arguments the GUI launches as usual. With arguments it runs headlessly,
sharing the exact same engine as the GUI.

```powershell
AutoClicker.exe --run sequence.acseq --repeat 10
AutoClicker.exe --profile "Farm loop" --until-stopped
AutoClicker.exe --list
AutoClicker.exe --version
AutoClicker.exe --help
```

| Option | |
|---|---|
| `--run <file>` | Run a `.acseq` sequence file |
| `--profile <name>` | Run a saved profile |
| `--list` | List saved profiles |
| `--repeat <n>` | Run the sequence n times |
| `--until-stopped` | Repeat until Ctrl+C |
| `--background` | Use background mode |
| `--jitter-px <n>` / `--jitter-pct <n>` | Position / timing randomisation |
| `--start-delay <s>` | Wait before starting |
| `--version` | Print the version |
| `--help` | Print usage |

Exit codes: `0` success (including a clean Ctrl+C), `1` runtime failure, `2` bad usage.
Ctrl+C stops cleanly and always releases any held mouse button.

## MCP mode — let an AI assistant drive it

The same engine is exposed over the [Model Context Protocol](https://modelcontextprotocol.io)
so an AI assistant can inspect and run your sequences:

```powershell
AutoClicker.exe --mcp --stdio        # JSON-RPC over stdin/stdout (Claude Desktop, VS Code Copilot)
AutoClicker.exe --mcp                # JSON-RPC over a named pipe (custom clients)
```

Tools exposed: `list_profiles`, `list_sequences`, `read_sequence`,
`create_sequence`, `run_sequence` (returns immediately; poll `get_state`),
`stop_run`, `get_state`.

**Claude Desktop** (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "autoclicker": {
      "command": "AutoClicker.exe",
      "args": ["--mcp", "--stdio"]
    }
  }
}
```

**VS Code Copilot** (`.vscode/mcp.json`):

```json
{
  "servers": {
    "autoclicker": {
      "type": "stdio",
      "command": "AutoClicker.exe",
      "args": ["--mcp", "--stdio"]
    }
  }
}
```

Mainstream MCP clients speak **stdio**, which is what `--stdio` provides. The named
pipe transport (`--pipe-name <name>`, defaulting to `AutoClicker.mcp.<username>`) is
there for clients that support custom transports; its ACL grants access to the
current Windows user only.

> MCP mode is only reachable from the command line, never by double-clicking the app:
> it hands control of real mouse/keyboard input to another process, so it exists only
> when you explicitly start it. Set `AUTOCLICKER_MCP_TRACE=1` to log the protocol to
> stderr.

## Build

Requires the **.NET 10 SDK** (targets `net10.0-windows10.0.17763.0`).

```powershell
dotnet build -c Release
dotnet run            # or launch bin\Release\net10.0-windows\AutoClicker.exe
```

If `dotnet` isn't on your `PATH` (a per-user install), use
`& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`.

## Package (MSIX)

```powershell
pwsh -File packaging\Build-Package.ps1
```

Publishes self-contained `win-x64`, packs an MSIX, and signs it. The version is read
from `AutoClicker.csproj` and injected into the manifest, so you only edit the version
in one place. Signing: pass `-SignThumbprint <thumbprint>` (or set
`$env:SIGN_THUMBPRINT`) to use a store certificate, or leave it unset to generate a
self-signed certificate. The certificate subject must match the `Publisher` in
`packaging\AppxManifest.xml` — the script fails fast if it doesn't.

## Versioning

This project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The
version lives in `AutoClicker.csproj`; see [CHANGELOG.md](CHANGELOG.md) for what
changed in each release.

## License

MIT — see [LICENSE](LICENSE).
