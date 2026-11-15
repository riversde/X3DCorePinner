# Changelog

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
