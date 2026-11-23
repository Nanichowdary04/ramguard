# RamGuard

A small Windows tray app that keeps RAM for the apps you're actually using.

- **Watches what you use.** Every 2 seconds it notes which app is in front of you. An app you haven't
  had open for *Idle after* minutes (default 10) counts as **idle**.
- **Dev servers are handled properly.** Each `npm run dev` / `python …` you start from a terminal gets its
  own row (shown with its port). It stays "in use" while a browser tab is connected to it.
- **Close idle apps…** shows the idle apps with how much memory each frees; untick anything you want to keep.
  Apps with a window are asked to close (so they can still ask you to save). Apps without a window are ended.
  **WSL & Docker** is stopped with `docker desktop stop` + `wsl --shutdown`.
- **Trim idle apps** moves idle apps' memory out of RAM without closing them. It comes back when you use them.
- **Rules per app:** *Ask* (offered when cleaning), *Keep* (never closed), *Auto-close* (closed automatically
  when idle while **Auto mode** is on). Right-click a row for the same options or to close one app now.
- **Never touched:** Windows parts, antivirus, services, terminals (PowerShell, bash, cmd), and RamGuard itself.
- **Auto mode:** checks every minute, closes idle *Auto-close* apps, and trims idle apps when RAM is above 80%.
- **Game mode** (on by default): when a game is in front of you, RamGuard slows down every other app (below-normal
  priority + Windows efficiency mode, so on hybrid Intel CPUs they move to the efficiency cores), frees their memory
  (and keeps RAM under 70% while you play), and switches Windows to **Best performance**. The game itself is never
  touched (anti-cheat friendly), and voice chat, music, overlays, launchers and anti-cheat are never slowed. Everything
  goes back to normal when the game closes, and if RamGuard stops mid-game, the next start restores it. Games are found
  from Windows' own Game Bar list, game folders (Steam, Riot, Epic, Xbox, GOG, EA, Ubisoft, .minecraft) and a built-in
  list. Right-click any app → **This is a game** to add one.
- **RAM graph:** the last hour of RAM use, with a green dot each time memory was freed and a green band while Game
  mode was on, plus today's totals ("Today: 4.2 GB freed (37×) · 1 game boosted").
- **Growing apps:** apps whose memory climbs steadily for 45+ minutes (300 MB or more, 150 MB/h or faster, still
  rising) are marked "Growing" in red, with a note that restarting them gives the memory back.
- **Startup apps…:** every app that starts with Windows (your list, all users, startup folders, Store apps), with an
  on/off switch that works the same way as Task Manager (nothing is uninstalled). Broken entries can be removed.
  Windows Security, driver helpers and anti-cheat are locked on. All-users entries ask Windows for admin.
- Choosing **Auto-close** for an app always asks first, and the mouse wheel can't change a rule by accident.
- Closing the window keeps it in the tray (the icon shows your RAM %). Right-click the tray icon → Exit.

RamGuard itself uses about 25 MB.

## Install

Run `dist\RamGuard-Setup-1.0.0.exe`. It installs for your user only (no admin needed) to
`%LOCALAPPDATA%\Programs\RamGuard`, adds a Start menu entry, a desktop shortcut, and (optionally) starts
RamGuard in the tray when you sign in. Uninstall it from **Settings → Apps → Installed apps → RamGuard**.

## Build

Uses the C# compiler that comes with Windows. No SDK or downloads:

```
powershell -ExecutionPolicy Bypass -File build.ps1              # bin\RamGuard.exe
powershell -ExecutionPolicy Bypass -File build.ps1 -Installer   # + dist\RamGuard-Setup-1.0.0.exe (needs Inno Setup 6)
```

The app icon is drawn by `tools\MakeIcon.cs` into `assets\RamGuard.ico`.

## Test

```
bin\RamGuard.exe --selftest bin\selftest.txt
```

It only closes test processes it starts itself (`rg-dummy.exe` and a test `node.exe`).
`bin\RamGuard.exe --snapshot main.png dialog.png` renders the UI to images.

Settings are saved in `%APPDATA%\RamGuard\settings.txt`.
