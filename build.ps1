<#
.SYNOPSIS
    Build X3D Core Pinner and package the portable release ZIP.

.DESCRIPTION
    1. Checks for the .NET 8 SDK (offers the winget command if missing).
    2. Runs the core self-tests (tests\CoreCheck).
    3. Publishes a self-contained, single-file win-x64 build (no .NET needed on target PCs).
    4. Adds README, LICENSE, CHANGELOG and portable.flag.
    5. Creates dist\X3DCorePinner-v<version>-win-x64.zip (portable) and a .sha256 file.
    6. If Inno Setup 6 is installed, creates dist\X3DCorePinner-v<version>-setup.exe (+ .sha256).
       Install Inno Setup with:  winget install JRSoftware.InnoSetup

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SkipTests
    .\build.ps1 -RequireInstaller   # fail if Inno Setup is missing (CI)
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$RequireInstaller
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Write-Step { param([string]$Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }

# Pure .NET SHA-256 (Get-FileHash can be unavailable when 5.1 inherits PS7 module paths).
function Get-Sha256 {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($fs))).Replace('-', '') }
    finally { $fs.Dispose(); $sha.Dispose() }
}

# ------------------------------------------------------------------ SDK check
Write-Step 'Checking .NET 8 SDK'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$sdks = if ($dotnet) { & dotnet --list-sdks 2>$null } else { @() }
if (-not ($sdks | Where-Object { $_ -match '^(8|9|10)\.' })) {
    Write-Host '.NET 8 SDK (or newer) not found.' -ForegroundColor Red
    Write-Host 'Install it with:  winget install Microsoft.DotNet.SDK.8' -ForegroundColor Yellow
    Write-Host 'Then open a NEW terminal and run build again.' -ForegroundColor Yellow
    exit 1
}
Write-Host ($sdks -join "`n")

# ------------------------------------------------------------------ version
[xml]$proj = Get-Content 'src\X3DCorePinner\X3DCorePinner.csproj'
$version = @($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { $version = '0.0.0' }
Write-Host "Version: $version"

# ------------------------------------------------------------------ tests
if (-not $SkipTests) {
    Write-Step 'Running core self-tests'
    & dotnet run --project 'tests\CoreCheck\CoreCheck.csproj' -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Core self-tests failed ($LASTEXITCODE)." }
}

# ------------------------------------------------------------------ publish
$dist = Join-Path $PSScriptRoot 'dist'
$out  = Join-Path $dist 'X3DCorePinner'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

Write-Step "Publishing self-contained single-file ($Runtime)"
& dotnet publish 'src\X3DCorePinner\X3DCorePinner.csproj' `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# ------------------------------------------------------------------ package
Write-Step 'Packaging'
Copy-Item 'README.md', 'LICENSE', 'CHANGELOG.md' -Destination $out
Set-Content -Path (Join-Path $out 'portable.flag') -Encoding UTF8 -Value @'
Portable mode marker for X3D Core Pinner.
While this file exists, settings, logs and reports are stored in the "Data" folder next to X3DCorePinner.exe.
Delete it to store them in %APPDATA% / %LOCALAPPDATA%\X3DCorePinner instead.
'@

# Pure .NET (no Compress-Archive / Get-FileHash): works even when Windows PowerShell 5.1
# is launched from PowerShell 7 and inherits a PSModulePath that breaks module autoload.
$zip = Join-Path $dist "X3DCorePinner-v$version-$Runtime.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$hash = Get-Sha256 $zip
Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ASCII

# ------------------------------------------------------------------ installer (Inno Setup 6)
Write-Step 'Building installer (Inno Setup 6)'
$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}

$setup = $null
if ($iscc) {
    & $iscc /Qp "/DMyAppVersion=$version" "/DSourceDir=$out" "/DOutputDir=$dist" (Join-Path $PSScriptRoot 'installer\X3DCorePinner.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed ($LASTEXITCODE)." }
    $setup = Join-Path $dist "X3DCorePinner-v$version-setup.exe"
    $setupHash = Get-Sha256 $setup
    Set-Content -Path "$setup.sha256" -Value "$setupHash  $(Split-Path $setup -Leaf)" -Encoding ASCII
} else {
    $msg = 'Inno Setup 6 not found - installer skipped. Install it with:  winget install JRSoftware.InnoSetup'
    if ($RequireInstaller) { throw $msg }
    Write-Host $msg -ForegroundColor Yellow
}

$exe = Get-Item (Join-Path $out 'X3DCorePinner.exe')
Write-Host ''
Write-Host "EXE : $($exe.FullName)  ($([math]::Round($exe.Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "ZIP : $zip" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Green
if ($setup) {
    Write-Host "SETUP : $setup  ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
    Write-Host "SHA256: $setupHash" -ForegroundColor Green
}
