# Changelog

## 1.1.0 - 2026-11-15
- **Windows installer** (`X3DCorePinner-vX.Y.Z-setup.exe`, Inno Setup): installs to `C:\Program Files\X3DCorePinner`, Start menu entry, optional desktop shortcut, optional *Start with Windows*, clean upgrades and a full uninstaller. The portable ZIP is still published.
- Uninstall exits the app cleanly (all pinned apps restored), removes the logon task and `%ProgramData%\X3DCorePinner`, and optionally your settings and logs.
- New headless switches for scripts and installers: `--shutdown`, `--enable-startup`, `--disable-startup`, `--restore-all`, `--cleanup`.
- Includes the 1.0.1 security hardening below.

## 1.0.1 - 2026-11-15
Security hardening (no feature changes).

- Start with Windows now refuses to register when the app folder can be modified without admin rights (prevents a silent admin-rights bypass through the elevated logon task). Install to `C:\Program Files\X3DCorePinner`.
- The Task Scheduler XML is written to an admin-only folder (`%ProgramData%\X3DCorePinner`) instead of `%TEMP%`.
- PresentMon is stored in the admin-only `%ProgramData%\X3DCorePinner\Tools\PresentMon` and pinned by SHA-256 at download; a changed file is deleted and downloaded again instead of being executed.
- Native WPF libraries ship next to the exe instead of being extracted to `%TEMP%\.net` at run time.
- `explorer.exe` is started by absolute path.
- New *App location* check in the system audit.

## 1.0.0 - 2026-11-15
First public release.

- Detects the real L3 topology (V-Cache CCD vs frequency CCD) on dual-CCD Ryzen X3D CPUs (7950X3D, 7900X3D, 9950X3D, 9900X3D).
- Pins your chosen background apps to the frequency CCD only while a listed game runs; restores each app's original affinity when the game closes, when you pause, on exit and on Windows sign-out.
- Never touches the game, Windows core processes, security software or anti-cheat services.
- Tray icon with Windows notifications (protection active, game detected, session ended).
- Start with Windows through a Task Scheduler logon task (elevated, no UAC prompt).
- Editable game and background-app lists with defaults and a running-process picker.
- Live per-CCD load and core-parking view.
- System audit: V-Cache driver, power plan, Game Mode, background capture, MPO, windowed-game optimizations, overlays.
- Top CPU users sampler.
- Frame analysis with Intel PresentMon (downloaded on demand): FPS, 1% lows, CPU/GPU busy per frame, display latency, present mode, bottleneck verdict.
- Portable mode (portable.flag): settings, logs and reports live next to the exe.
