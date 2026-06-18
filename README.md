# GeforceNow-Fullscreen

A lightweight Windows launcher that runs NVIDIA GeForce NOW in true borderless fullscreen, with an animated splash screen on startup.

## What it does

- Strips the GeForce NOW title bar and window chrome at launch
- Resizes the window to cover the full screen (no taskbar bleed)
- Shows an animated NVIDIA splash (GIF) while GFN loads, with a smooth fade-to-black transition
- Prevents GFN from stealing focus during the splash using `LockSetForegroundWindow`
- Registers itself to auto-start with Windows

## Requirements

- Windows 10/11 (built for 22H2+)
- .NET 9 SDK
- GeForce NOW installed via the default path (`%LOCALAPPDATA%\NVIDIA Corporation\GeForceNOW\CEF\`)

## Build

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The output is a single `GfnFullscreen.exe` in the publish folder.

## Usage

```bash
# Register auto-start with Windows
GfnFullscreen.exe --install

# Remove auto-start
GfnFullscreen.exe --uninstall

# Just run it (launches GFN in fullscreen)
GfnFullscreen.exe
```

## How it works

1. On launch, displays `nvidia.gif` as a fullscreen splash
2. Locks foreground window ownership so GFN can't steal focus
3. Starts GeForce NOW in the background and hides its window immediately
4. When the GIF finishes, fades to black and hands off to GFN fullscreen
5. If GFN is already running, just strips its title bar and exits

## Customization

Replace `nvidia.gif` with any GIF to use as your splash screen. The launcher reads frame delays from the GIF metadata and plays it at native speed.
