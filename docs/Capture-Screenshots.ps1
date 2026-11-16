<#
.SYNOPSIS
    Capture README screenshots of X3D Core Pinner (one PNG per page).

.DESCRIPTION
    - Opens (or brings up) the installed app, resizes the window to a fixed size,
      switches pages through UI Automation and captures only the window
      (PrintWindow with full DirectX content, cropped to the visible frame).
    - Output: docs\screenshots\01-dashboard.png ... 06-about.png
    - Must run from an ADMIN PowerShell: the app is elevated, and Windows blocks
      non-elevated scripts from automating elevated windows.
    - Review every image before committing: Games / Background apps show your
      running process names, Diagnostics shows your system audit.

.EXAMPLE
    .\docs\Capture-Screenshots.ps1
    .\docs\Capture-Screenshots.ps1 -Width 1400 -Height 900
    .\docs\Capture-Screenshots.ps1 -Pages 'Dashboard','Diagnostics'
#>
[CmdletBinding()]
param(
    [string]$ExePath = 'C:\Program Files\X3DCorePinner\X3DCorePinner.exe',
    [string]$OutDir  = (Join-Path $PSScriptRoot 'screenshots'),
    [int]$Width  = 1280,
    [int]$Height = 820,
    [string[]]$Pages = @('Dashboard', 'Games', 'Background apps', 'Diagnostics', 'Frame analysis', 'About')
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('ScreenCap' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ScreenCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
'@
}

# Physical pixels for GetWindowRect / PrintWindow (per-monitor DPI aware v2 = -4).
[void][ScreenCap]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))

function Get-AppWindow {
    $p = Get-Process -Name X3DCorePinner -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    if ($p) { return $p.MainWindowHandle }
    return [IntPtr]::Zero
}

# ---------------------------------------------------------------- open the window
$hwnd = Get-AppWindow
if ($hwnd -eq [IntPtr]::Zero) {
    if (-not (Test-Path $ExePath)) { throw "App not found at $ExePath. Install it or pass -ExePath." }
    Write-Host 'Opening X3D Core Pinner window...' -ForegroundColor Cyan
    # A second launch tells the running (tray) instance to show its window.
    Start-Process -FilePath $ExePath
    $deadline = (Get-Date).AddSeconds(20)
    while ($hwnd -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $hwnd = Get-AppWindow
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'Could not find the X3D Core Pinner window.' }
    Start-Sleep -Seconds 2
}

[void][ScreenCap]::ShowWindow($hwnd, 9)          # SW_RESTORE
[void][ScreenCap]::MoveWindow($hwnd, 60, 60, $Width, $Height, $true)
[void][ScreenCap]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 800

# ---------------------------------------------------------------- UI Automation helpers
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$ctProp = [System.Windows.Automation.AutomationElement]::ControlTypeProperty
$nameProp = [System.Windows.Automation.AutomationElement]::NameProperty

function Find-Element {
    param($ControlType, [string]$Name)
    $cond = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($ctProp, $ControlType),
        [System.Windows.Automation.PropertyCondition]::new($nameProp, $Name))
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Select-Page {
    param([string]$Name)
    $tab = Find-Element ([System.Windows.Automation.ControlType]::TabItem) $Name
    if (-not $tab) { throw "Page '$Name' not found." }
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Invoke-ButtonIfPresent {
    param([string]$Name)
    $btn = Find-Element ([System.Windows.Automation.ControlType]::Button) $Name
    if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    return $false
}

function Save-WindowShot {
    param([string]$Path)
    $r = [ScreenCap+RECT]::new()
    [void][ScreenCap]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    $bmp = [System.Drawing.Bitmap]::new($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][ScreenCap]::PrintWindow($hwnd, $hdc, 2)   # PW_RENDERFULLCONTENT (DirectX/WPF)
    $g.ReleaseHdc($hdc)
    $g.Dispose()

    # Crop the invisible resize borders (DWMWA_EXTENDED_FRAME_BOUNDS = 9).
    $f = [ScreenCap+RECT]::new()
    if ([ScreenCap]::DwmGetWindowAttribute($hwnd, 9, [ref]$f, 16) -eq 0) {
        $crop = [System.Drawing.Rectangle]::new($f.Left - $r.Left, $f.Top - $r.Top, $f.Right - $f.Left, $f.Bottom - $f.Top)
        if ($crop.Width -gt 0 -and $crop.Height -gt 0 -and $crop.Right -le $w -and $crop.Bottom -le $h) {
            $c = $bmp.Clone($crop, $bmp.PixelFormat)
            $bmp.Dispose()
            $bmp = $c
        }
    }
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# ---------------------------------------------------------------- capture
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$i = 0
foreach ($page in $Pages) {
    $i++
    Select-Page $page
    Start-Sleep -Milliseconds 1200          # live CCD bars and lists update
    if ($page -eq 'Diagnostics') {
        if (Invoke-ButtonIfPresent 'Run audit') { Start-Sleep -Seconds 5 }
    }
    $slug = ($page.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    $file = Join-Path $OutDir ('{0:00}-{1}.png' -f $i, $slug)
    Save-WindowShot $file
    Write-Host "Saved $file" -ForegroundColor Green
}

Select-Page 'Dashboard'
Write-Host ''
Write-Host "Done. Review the images in $OutDir before committing (they show your process names and system audit)." -ForegroundColor Yellow
