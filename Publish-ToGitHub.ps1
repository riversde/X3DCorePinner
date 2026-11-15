<#
.SYNOPSIS
    Publish X3D Core Pinner to GitHub (first commit + repo creation + optional release tag).

.DESCRIPTION
    1. Checks git and GitHub CLI (gh) are installed and you are logged in.
    2. Replaces the "PRFRAG" placeholder in repo links with your GitHub username.
    3. Moves ci\build.yml to .github\workflows\build.yml (CI build + release).
    4. git init (main), first commit (uses your GitHub no-reply email if git has none set).
    5. Creates the public repo <you>/X3DCorePinner and pushes.
    6. -Release: tags v<version> and pushes the tag -> GitHub Actions builds and publishes the ZIP.

.EXAMPLE
    .\Publish-ToGitHub.ps1             # repo + first push
    .\Publish-ToGitHub.ps1 -Release    # repo + push + v1.0.0 release
    .\Publish-ToGitHub.ps1 -Private    # private repo instead of public
#>
[CmdletBinding()]
param(
    [string]$RepoName = 'X3DCorePinner',
    [switch]$Release,
    [switch]$Private
)

# Continue: native tools (git, gh) write progress to stderr; failures are checked via exit codes.
$ErrorActionPreference = 'Continue'
Set-Location -Path $PSScriptRoot

function Write-Step { param([string]$Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments, [switch]$AllowFail)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0 -and -not $AllowFail) { throw "$Exe $($Arguments -join ' ') failed ($LASTEXITCODE)." }
    return $LASTEXITCODE
}

# ------------------------------------------------------------------ tools
Write-Step 'Checking git and GitHub CLI'
foreach ($tool in @(@{ Cmd = 'git'; Pkg = 'Git.Git' }, @{ Cmd = 'gh'; Pkg = 'GitHub.cli' })) {
    if (-not (Get-Command $tool.Cmd -ErrorAction SilentlyContinue)) {
        Write-Host "$($tool.Cmd) not found. Install it with:  winget install $($tool.Pkg)" -ForegroundColor Red
        Write-Host 'Then open a NEW terminal and run this script again.' -ForegroundColor Yellow
        exit 1
    }
}
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Not logged in to GitHub CLI. Run:  gh auth login   (GitHub.com > HTTPS > Login with a web browser)' -ForegroundColor Red
    exit 1
}
$login = (& gh api user --jq .login).Trim()
$userId = (& gh api user --jq .id).Trim()
if (-not $login) { throw 'Could not read your GitHub username from gh.' }
Write-Host "GitHub user: $login" -ForegroundColor Green

# ------------------------------------------------------------------ links
Write-Step "Pointing repo links to github.com/$login/$RepoName"
$files = @('src\X3DCorePinner.Core\Platform.cs', 'src\X3DCorePinner\MainWindow.xaml', 'README.md')
foreach ($f in $files) {
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $f))
    $new = $text.Replace('github.com/PRFRAG/X3DCorePinner', "github.com/$login/$RepoName")
    if ($new -ne $text) {
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot $f), $new, (New-Object Text.UTF8Encoding($false)))
        Write-Host "  updated $f"
    }
}

# ------------------------------------------------------------------ CI file
$ciSrc = Join-Path $PSScriptRoot 'ci\build.yml'
$ciDst = Join-Path $PSScriptRoot '.github\workflows\build.yml'
if (Test-Path $ciSrc) {
    Write-Step 'Moving ci\build.yml to .github\workflows\build.yml'
    New-Item -ItemType Directory -Path (Split-Path $ciDst) -Force | Out-Null
    Move-Item $ciSrc $ciDst -Force
    if (-not (Get-ChildItem (Join-Path $PSScriptRoot 'ci') -Force)) { Remove-Item (Join-Path $PSScriptRoot 'ci') -Force }
}

# ------------------------------------------------------------------ version
[xml]$proj = Get-Content 'src\X3DCorePinner\X3DCorePinner.csproj'
$version = @($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]

# ------------------------------------------------------------------ git
Write-Step 'Creating local git repository and first commit'
if (-not (Test-Path (Join-Path $PSScriptRoot '.git'))) {
    Invoke-Native git @('init', '-b', 'main') | Out-Null
}
$name = (& git config user.name)
$mail = (& git config user.email)
if (-not $name) { Invoke-Native git @('config', 'user.name', $login) | Out-Null }
if (-not $mail) {
    # GitHub no-reply address keeps your personal email out of public history.
    Invoke-Native git @('config', 'user.email', "$userId+$login@users.noreply.github.com") | Out-Null
}

Invoke-Native git @('add', '-A') | Out-Null
& git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    $msgFile = Join-Path $env:TEMP 'x3d_commit_msg.txt'
    @"
X3D Core Pinner v$version - initial release

Keeps the AMD Ryzen X3D V-Cache CCD free for games by pinning background
apps to the frequency CCD while a listed game runs; restores on exit.
WPF tray app with live CCD view, system audit, top CPU users and
PresentMon frame analysis. MIT license.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01GyA737g3Qx8P1ReL4DVnQi
"@ | Set-Content -Path $msgFile -Encoding UTF8
    Invoke-Native git @('commit', '-F', $msgFile) | Out-Null
    Remove-Item $msgFile -Force
    Write-Host 'Committed.' -ForegroundColor Green
} else {
    Write-Host 'Nothing new to commit.' -ForegroundColor Yellow
}

# ------------------------------------------------------------------ GitHub repo
Write-Step "Creating GitHub repo $login/$RepoName and pushing"
$visibility = if ($Private) { '--private' } else { '--public' }
& gh repo view "$login/$RepoName" *> $null
if ($LASTEXITCODE -ne 0) {
    Invoke-Native gh @('repo', 'create', "$login/$RepoName", $visibility, '--source', '.', '--remote', 'origin',
        '--description', 'Keep the Ryzen X3D V-Cache for your game: pins background apps to the frequency CCD while you play.',
        '--push') | Out-Null
} else {
    Write-Host 'Repo already exists - pushing to it.' -ForegroundColor Yellow
    & git remote get-url origin *> $null
    if ($LASTEXITCODE -ne 0) { Invoke-Native git @('remote', 'add', 'origin', "https://github.com/$login/$RepoName.git") | Out-Null }
    Invoke-Native git @('push', '-u', 'origin', 'main') | Out-Null
}
& gh repo edit "$login/$RepoName" --add-topic ryzen --add-topic x3d --add-topic amd --add-topic gaming --add-topic cpu-affinity --add-topic wpf --add-topic presentmon *> $null

# ------------------------------------------------------------------ release
if ($Release) {
    Write-Step "Tagging v$version (GitHub Actions will build and publish the release)"
    & git rev-parse "v$version" *> $null
    if ($LASTEXITCODE -ne 0) { Invoke-Native git @('tag', '-a', "v$version", '-m', "X3D Core Pinner v$version") | Out-Null }
    Invoke-Native git @('push', 'origin', "v$version") | Out-Null
}

Write-Host ''
Write-Host "Repo    : https://github.com/$login/$RepoName" -ForegroundColor Green
Write-Host "Actions : https://github.com/$login/$RepoName/actions" -ForegroundColor Green
if ($Release) { Write-Host "Release : https://github.com/$login/$RepoName/releases (ready in about 3-5 minutes)" -ForegroundColor Green }
