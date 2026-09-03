# NotifyLite

Windows tray app that reads system notifications and keeps a history. Optional custom toast UI.

## What is different in this Fork

This is a fork of [AfaqAhmad0/NotifyLite](https://github.com/AfaqAhmad0/NotifyLite).

What is different:

- Click tray icon to show notification history
- Cleaner UI design (flat dark mode, bigger text)
- Toggle to hide the floating notifications button
- Toggle to use native windows notifications (only use for notification history)
- Retain notification in history when dismissed
- Toggle to show count of unread notifications
- Setting for maximum number of notifications in history for a specific app (by app name or id)

Windows 10/11 (64-bit), .NET 8.

---

## Install

1. Download ZIP from [bingud/NotifyLite](https://github.com/bingud/NotifyLite) (**Code → Download ZIP**).
2. Extract, open **`Dist`**, run **`Install.bat`**. Approve the admin prompt (and **Run anyway** if Windows asks).
3. Start **NotifyLite** from the Start menu.
4. Allow notification access when Windows asks.

Needs `Dist\NotifyLite.msix` and `Dist\NotifyLite.cer`. If those are missing, build first (below).

Manual install (PowerShell as admin):

```powershell
cd Dist
certutil -addstore TrustedPeople .\NotifyLite.cer
Add-AppxPackage .\NotifyLite.msix
```

Uninstall: `Dist\Uninstall.ps1`.

## Screenshots

<p align="center">
  <img src="docs/assets/settings.png" width="48%" alt="Settings">
  <img src="docs/assets/custom taost.png" width="48%" alt="Custom toasts">
</p>

## What it does

Runs in the tray (hidden from Alt+Tab). Intercepts toasts via `UserNotificationListener` — MSIX install is required for that permission.

- Custom toasts: colors, fonts, size, opacity, position, sounds, per-app mute
- History from the tray (and optional floating badge)
- Click a toast/history item to open the source app

Right-click the tray icon → Settings.

## Build

Needs [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and the [Windows SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/) (`MakeAppx`, `SignTool`). From the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File Scripts\build-msix.ps1
```

Publishes, signs, installs, and copies the MSIX + cert into `Dist\`.

## License

MIT.
