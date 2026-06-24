#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Local pack + push to NuGet (uses $env:NUGET_API_KEY).
.DESCRIPTION
    Packs all Dali libraries and pushes them to nuget.org.
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

if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
    Write-Host "NUGET_API_KEY environment variable is not set." -ForegroundColor Red
    Write-Host "Set it with: `$env:NUGET_API_KEY = 'your-key-here'" -ForegroundColor Yellow
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
