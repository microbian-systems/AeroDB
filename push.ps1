#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Local pack + push to NuGet (uses $env:NUGET_API_KEY).
.DESCRIPTION
    Packs all AeroDB libraries and pushes them to nuget.org.
    Defaults to preview (alpha suffix). Use -Stable for release.
.PARAMETER VersionSuffix
    Version suffix for preview builds. Default: alpha.
    Ignored when -Stable is used.
.PARAMETER Stable
    Produce and push stable (release) packages with no suffix.
.EXAMPLE
    # Preview push
    ./push.ps1

    # Stable release
    ./push.ps1 -Stable
#>

param(
    [string]$VersionSuffix = "alpha",
    [switch]$Stable
)

$apiKey = $env:GITHUB_API_KEY_AeroDB ?? $env:NUGET_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    Write-Host "No NuGet API key found." -ForegroundColor Red
    Write-Host "Set: `$env:GITHUB_API_KEY_AeroDB = 'your-AeroDB-key'" -ForegroundColor Yellow
    Write-Host "Or:  `$env:NUGET_API_KEY = 'your-key-here'" -ForegroundColor Yellow
    exit 1
}

Write-Host "=== Step 1: Pack ===" -ForegroundColor Cyan
if ($Stable) {
    & "$PSScriptRoot/build/nuget-pack.ps1" -Stable
} else {
    & "$PSScriptRoot/build/nuget-pack.ps1" -VersionSuffix $VersionSuffix
}
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n=== Step 2: Publish ===" -ForegroundColor Cyan
& "$PSScriptRoot/build/nuget-publish.ps1"
