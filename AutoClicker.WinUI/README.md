# AutoClicker.WinUI (M1 skeleton)

Minimal WinUI 3 / Windows App SDK 2.5.1 shell. **Not shipped** — the shipping app is still the
WinForms `AutoClicker.exe`, and `AutoClicker.Core` remains the engine.

## Purpose

M1 proves three things before any UI is ported:

1. A WinUI TFM (`net10.0-windows10.0.19041.0`) can `ProjectReference` the
   `net10.0-windows10.0.17763.0` + `UseWindowsForms` engine library.
2. The XAML toolchain compiles `App.xaml` / `MainWindow.xaml` and generates the entry point
   with no hand-written `Main`.
3. The app **launches** with no installed Windows App Runtime and no MSIX.

## Run

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
& $dotnet build .\AutoClicker.WinUI\AutoClicker.WinUI.csproj -c Release
.\AutoClicker.WinUI\bin\Release\net10.0-windows10.0.19041.0\win-x64\AutoClicker.WinUI.exe
```

Deployment shape: **unpackaged** (`WindowsPackageType=None`) + **self-contained**
(`WindowsAppSDKSelfContained=true`, `SelfContained=true`). The WinAppSDK native DLLs, the
`resources.pri`, and the .NET runtime all land in the output folder, so nothing has to be
installed machine-wide.

## M2 notes

- `ExtendsContentIntoTitleBar` is `false`; `AppWindow.Resize` uses **physical** pixels.
- Nothing here consumes `AutoClicker.Core` yet. `InternalsVisibleTo` in the engine lists only
  `AutoClicker` and `AutoClicker.Tests`; M2 will need an `AutoClicker.WinUI` entry there.
