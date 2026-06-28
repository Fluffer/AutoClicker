# Auto Clicker

A lightweight Windows mouse auto-clicker (C# / WinForms, .NET 10). Click or hold at a
fixed point, or run a recorded **sequence of points** with per-point waits.

![Auto Clicker](docs/screenshot.png)

## Features

- **Click / Double-click / Hold** with any mouse button (left / right / middle)
- Adjustable interval (hours / mins / secs / ms)
- Repeat **N times** or **until stopped**
- **Single point** (current cursor or a picked X/Y) **or multi-point sequences**
- **Record by clicking** — hit *Record*, click each target, right-click or **F8** to finish
- Per-point **edit** (X / Y / button / type / wait) and reorder
- **Save / load** sequences as `.acseq` (JSON) for reuse
- **Background mode** (experimental) — posts clicks to the window under each point
  without moving your real cursor, so you can keep working on another monitor
  *(target-dependent — see notes below)*
- Global **F6** start/stop hotkey (rebindable F1–F12)
- High-DPI aware, custom icon, single self-contained build

## Install

Download `AutoClicker.msix` from the [latest release](../../releases/latest) and
double-click it. The package is signed with a trusted CA code-signing certificate,
so there's no trust prompt or SmartScreen block.

Or via PowerShell:

```powershell
Add-AppxPackage -Path AutoClicker.msix
```

## Background mode — what works

Background clicking uses `PostMessage(WM_*BUTTONDOWN/UP)` to the window under each
point. It works for many classic Win32 apps but **fails** on:

- Games using DirectInput / raw input (`WM_INPUT`)
- Chrome / Electron (events flagged `isTrusted: false`)
- UWP / WinUI / WPF (single window handle, internal hit-testing)
- Admin-elevated targets (blocked by UIPI)

For those, use normal (foreground) mode, which moves the cursor.

## Build

```powershell
dotnet build -c Release
dotnet run            # or launch bin\Release\net10.0-windows\AutoClicker.exe
```

## Package (MSIX)

```powershell
pwsh -File packaging\Build-Package.ps1
```

Publishes self-contained `win-x64`, packs an MSIX, and signs it. Edit
`$signThumbprint` in the script and the `Publisher` in `packaging\AppxManifest.xml`
to match your own code-signing certificate (they must be identical). Clear
`$signThumbprint` to fall back to a generated self-signed cert.

## License

MIT — see [LICENSE](LICENSE).
