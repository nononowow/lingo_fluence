#Requires -Version 5.0
<#
.SYNOPSIS
    Installs LingoFluence for the current user.
.DESCRIPTION
    Builds a self-contained executable (via build.ps1), copies it into the
    user's local app folder (%LOCALAPPDATA%\Programs\LingoFluence), and creates
    Start Menu and optional Desktop shortcuts. No administrator rights required.
.PARAMETER InstallDir
    Install location. Default: %LOCALAPPDATA%\Programs\LingoFluence
.PARAMETER Desktop
    Also create a Desktop shortcut.
.PARAMETER SkipBuild
    Reuse an existing .\publish output instead of rebuilding.
.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -Desktop
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\LingoFluence'),
    [switch]$Desktop,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publishDir = Join-Path $root 'publish'

if (-not $SkipBuild) {
    Write-Host "==> Building release executable..." -ForegroundColor Cyan
    & (Join-Path $root 'build.ps1') -Configuration Release -Output $publishDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$exeSource = Join-Path $publishDir 'LingoFluence.exe'
if (-not (Test-Path $exeSource)) {
    Write-Error "Published executable not found at $exeSource. Run without -SkipBuild first."
    exit 1
}

Write-Host "==> Installing to $InstallDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $InstallDir -Recurse -Force

$exeTarget = Join-Path $InstallDir 'LingoFluence.exe'

function New-Shortcut([string]$LinkPath, [string]$TargetPath) {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($LinkPath)
    $sc.TargetPath = $TargetPath
    $sc.WorkingDirectory = Split-Path $TargetPath -Parent
    $sc.IconLocation = $TargetPath
    $sc.Description = 'LingoFluence — German vocabulary study'
    $sc.Save()
}

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startLink = Join-Path $startMenu 'LingoFluence.lnk'
Write-Host "==> Creating Start Menu shortcut..." -ForegroundColor Cyan
New-Shortcut -LinkPath $startLink -TargetPath $exeTarget

if ($Desktop) {
    $desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'LingoFluence.lnk'
    Write-Host "==> Creating Desktop shortcut..." -ForegroundColor Cyan
    New-Shortcut -LinkPath $desktopLink -TargetPath $exeTarget
}

Write-Host ""
Write-Host "==> LingoFluence installed." -ForegroundColor Green
Write-Host "    Location: $exeTarget"
Write-Host "    Launch it from the Start Menu, or run the executable directly."
Write-Host "    To uninstall, delete '$InstallDir' and the shortcut(s)."
