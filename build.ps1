#Requires -Version 5.0
<#
.SYNOPSIS
    Builds LingoFluence into a self-contained Windows executable.
.DESCRIPTION
    Publishes the WPF app as a single self-contained win-x64 executable
    (no .NET runtime required on the target machine). Output lands in
    .\publish by default.
.PARAMETER Configuration
    Build configuration: Release (default) or Debug.
.PARAMETER Output
    Output directory for the published app. Default: .\publish
.PARAMETER Runtime
    Target runtime identifier. Default: win-x64
.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -Configuration Debug -Output .\out
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Output = 'publish',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'LingoFluence\LingoFluence.csproj'

Write-Host "==> Checking for .NET SDK..." -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET SDK was not found on PATH. Install it from https://dotnet.microsoft.com/download"
    exit 1
}
dotnet --version

$outPath = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

Write-Host "==> Publishing LingoFluence ($Configuration / $Runtime)..." -ForegroundColor Cyan
Write-Host "    Output: $outPath"

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $outPath

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

$exe = Join-Path $outPath 'LingoFluence.exe'
Write-Host ""
Write-Host "==> Build complete." -ForegroundColor Green
Write-Host "    Executable: $exe"
