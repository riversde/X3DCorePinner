@echo off
rem X3D Core Pinner - one-click build. Requires the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause
