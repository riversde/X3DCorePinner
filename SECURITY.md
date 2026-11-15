# Security

X3D Core Pinner runs with administrator rights (Windows requires them to change the CPU affinity of other programs). This page explains what it does with them and how it is protected.

## What it does and does not do

- Changes only the CPU affinity (and optionally the priority) of the background apps **you list**, only while a listed game runs, and restores the original values afterwards.
- Never opens or modifies game processes. Windows core processes, security software and anti-cheat services are on a built-in skip list that the user cannot override.
- **No telemetry, no accounts, no analytics.** Nothing about you or your PC is sent anywhere.
- The only network access is the optional **Frame analysis** download of Intel PresentMon over HTTPS from `api.github.com` / `github.com/GameTechDev`.
- Settings, logs and reports stay on your PC (`Data` folder in portable mode, otherwise `%APPDATA%` / `%LOCALAPPDATA%\X3DCorePinner`). Logs contain process names, timings and your Windows account name (when Start with Windows is set up) - nothing else.

## Hardening

| Risk for an elevated app | Mitigation |
|---|---|
| Elevated logon task launching an exe that a non-admin program can replace | *Start with Windows* is refused unless the exe and its folder are writable only by Administrators/SYSTEM/TrustedInstaller. Install to `C:\Program Files\X3DCorePinner`. |
| Task XML swapped between write and registration | XML written to the admin-only `%ProgramData%\X3DCorePinner`, deleted after use. |
| Downloaded helper (PresentMon) replaced before it runs elevated | Stored in admin-only `%ProgramData%\X3DCorePinner\Tools\PresentMon` (inheritance removed, Users read/execute only) and SHA-256 pinned at download; a mismatch deletes the file instead of running it. Download URL must be under `github.com/GameTechDev/`. |
| Native DLLs extracted to a user-writable temp folder | Single-file publish without self-extraction; native libraries ship next to the exe. |
| Tool hijacking through PATH or the app folder | `sc.exe`, `powercfg.exe`, `schtasks.exe` and `explorer.exe` are started by absolute path. |
| Running from a user-writable folder in general | The system audit flags it (*App location*). |

Config files (game and app lists) are plain JSON with no executable paths, so tampering with them can at most change which listed processes are pinned - and never protected processes.

## Known limitations

- Releases are not code-signed yet, so SmartScreen warns on first run. Verify the SHA-256 published with each release, or build from source (`build.cmd`).
- A second program could pre-create the app's single-instance mutex and stop it from starting (denial of service only).

## Reporting a vulnerability

Please open a private security advisory on the GitHub repository (Security > Report a vulnerability) instead of a public issue.
