# GeforceNow-Fullscreen

A lightweight Windows launcher that runs NVIDIA GeForce NOW in true borderless fullscreen, with an animated splash screen on startup.

## What it does

- Strips the GeForce NOW title bar and window chrome at launch
- Resizes the window to cover the full screen (no taskbar bleed)
- Plays the NVIDIA splash through MPV while GFN loads, holding the final black frame until it is ready
- Keeps the MPV splash above GFN during startup
- Registers itself to auto-start with Windows

## Requirements

- Windows 11 22H2 or newer
- .NET 9 SDK (only needed to build from source)
- MPV at `C:\Program Files\MPV Player\mpv.exe` (the GIF splash is used as a fallback)
- GeForce NOW installed via the default path (`%LOCALAPPDATA%\NVIDIA Corporation\GeForceNOW\CEF\`)

## Build

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The publish folder contains `GfnFullscreen.exe`, supporting Windows libraries, `nvidia-splash.mp4`, `nvidia.gif`, `GeForceNOW.ico`, and `setup.ps1`. Copy the entire folder to the Ally; keep the files together.

## Setup

1. **Place the files** -- Put the entire published output in any folder (e.g. `C:\GfnFullscreen\`).

2. **Create the desktop shortcut** -- In PowerShell, run `.\setup.ps1` from the published folder. It creates **GeForce NOW** on the desktop with the original NVIDIA icon. Run `.\setup.ps1 -EnableAutoStart` instead if you also want the launcher to start at Windows sign-in.

3. **Use the shortcut** -- Double-click **GeForce NOW**. You can remove NVIDIA's separate desktop shortcut if you prefer one icon. NVIDIA updates may recreate its shortcut, but the separately named launcher shortcut should remain in place.

The icon is a multi-resolution copy extracted from the installed GeForce NOW executable.

## Usage

```bash
GfnFullscreen.exe              # Launch GFN in borderless fullscreen
GfnFullscreen.exe --install    # Register auto-start with Windows
GfnFullscreen.exe --uninstall  # Remove auto-start
```

## How it works

1. Before starting GeForce NOW, changes `nv-custom-black-window=true` to `false` in `GeForceNOW.json` if an update reset it. An already running session is not interrupted; the setting is applied on the next launch after GeForce NOW exits.
2. Plays `nvidia-splash.mp4` fullscreen through MPV while GeForce NOW starts, and holds the final frame until its window is ready. Repeat shortcut launches also play the splash.
3. Removes the main window's chrome and sizes it to the screen, watching for window changes and updater restarts for up to three minutes after exit.
4. A second shortcut launch tells the running launcher to bring GeForce NOW forward or start it again. The launcher does not repeatedly restore a window you minimized.

If a GeForce NOW update replaces its own desktop shortcut, use the separate **GeForce NOW** shortcut. Changes to this source only take effect after rebuilding and replacing the launcher executable on the Ally.

## Customization

Replace `nvidia-splash.mp4` with a roughly three-second video ending in black to customize the MPV splash. `nvidia.gif` remains the fallback when MPV or the video is unavailable.
