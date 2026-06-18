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
- .NET 9 SDK (only needed to build from source)
- GeForce NOW installed via the default path (`%LOCALAPPDATA%\NVIDIA Corporation\GeForceNOW\CEF\`)

## Build

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The output is a single `GfnFullscreen.exe` in the publish folder. Copy `nvidia.gif` into the same folder as the exe.

## Setup

1. **Place the files** -- Put `GfnFullscreen.exe` and `nvidia.gif` in any folder (e.g. `C:\GfnFullscreen\`).

2. **Register auto-start** (optional) -- Run once to launch GFN in fullscreen automatically on every boot:
   ```
   GfnFullscreen.exe --install
   ```

3. **Replace the desktop shortcut** (optional) -- If you want the GeForce NOW desktop shortcut to use the launcher instead of the stock app:
   - Right-click the **NVIDIA GeForce NOW** shortcut on your desktop and select **Properties**
   - Change the **Target** to the full path of `GfnFullscreen.exe` (e.g. `C:\GfnFullscreen\GfnFullscreen.exe`)
   - Click **OK**

   Now double-clicking the shortcut launches GFN through the fullscreen launcher with the splash screen.

4. **Run it** -- Double-click the shortcut or exe. The splash plays, then GFN appears in borderless fullscreen.

## Usage

```bash
GfnFullscreen.exe              # Launch GFN in borderless fullscreen
GfnFullscreen.exe --install    # Register auto-start with Windows
GfnFullscreen.exe --uninstall  # Remove auto-start
```

## How it works

1. On launch, displays `nvidia.gif` as a fullscreen splash
2. Locks foreground window ownership so GFN can't steal focus
3. Starts GeForce NOW behind the splash (visible to the OS but covered by the splash)
4. When the GIF finishes, fades to black and hands off to GFN fullscreen
5. If GFN is already running, just strips its title bar and exits

## Customization

Replace `nvidia.gif` with any GIF to use as your splash screen. The launcher reads frame delays from the GIF metadata and plays it at native speed.
