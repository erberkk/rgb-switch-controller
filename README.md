# PC Control

One button that turns every RGB light and screen in a PC off, and a second press that brings each of them back exactly as it was.

PC Control doesn't talk to the hardware itself. It drives each vendor's own app through the same path that app's UI uses. Before switching off it saves the current state, and it restores that state when switching back on. The UI is in Turkish.

## Supported setups

| Device | Vendor app | How it is controlled | Off / on |
|---|---|---|---|
| Lian Li wireless TL fans, wireless Strimer, TL LCD fan screen | L-Connect 3 (2.1.x) | The local L-Connect service API the L-Connect UI uses (`127.0.0.1:11021`), resending the UI's last lighting requests | brightness 0 ↔ saved brightness; LCD brightness 0 ↔ saved |
| Gigabyte motherboard + RAM, synced in RGB Fusion | GIGABYTE Control Center | UI Automation plus a real click on the RGB Fusion page, checked against GCC's `usdata2.xml` | `OFF` ↔ saved effect (colour, speed and brightness are kept by GCC) |
| Sapphire Nitro+/Pure GPU | Sapphire TRIXX 11.0 | Click on the Glow "RGB effect style" list, checked by reading the row highlight | `Turn off` ↔ saved style |
| TRYX Panorama SE cooler screen | KANALI 2.3.x | Click on the page's "Screen" switch, checked against KANALI's `store.json` | screen off ↔ on |

Devices whose app isn't installed are hidden. If a window doesn't look the way the controller expects, that device reports an error instead of clicking somewhere else. TRIXX and KANALI are driven by position within their own windows, so a new version of either app may need new coordinates.

## Build and install

Requirements: Windows 10/11, .NET Framework 4.8, and Visual Studio Build Tools with MSBuild.

```powershell
.\build.ps1                      # downloads the .NET 4.8 reference assemblies on first run
.\bin\PcControl.exe --install    # one UAC prompt
```

The vendor apps run elevated, so PcControl has to as well. `--install` does three things:
- copies the exe to `C:\Program Files\PcControl`;
- registers two "run with highest privileges" scheduled tasks, `PcControl` and `PcControl Toggle`;
- adds a desktop shortcut.

After that, launching the app doesn't prompt again. To update, rebuild and run `--install` again.

## Usage

- Window: press the power button. Closing the window hides the app to the tray.
- Tray: right-click the icon to toggle lights or exit.
- Without a window: `PcControl.exe --toggle`, `--off` or `--on`.

Saved states are stored in `%LOCALAPPDATA%\PcControl\state.json`.

## Adding a device

Implement `IDeviceController` (`src/Core/IDeviceController.cs`) and register it in `src/Controllers/Controllers.cs`. `TurnOffAsync` returns the snapshot that `TurnOnAsync` later receives.
