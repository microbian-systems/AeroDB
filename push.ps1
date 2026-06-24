#!/usr/bin/env pwsh

if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
    Write-Host "NUGET_API_KEY environment variable is not set." -ForegroundColor Red
    Write-Host "Set it with: `$env:NUGET_API_KEY = 'your-key-here'" -ForegroundColor Yellow
    exit 1
}

Write-Host "=== Step 1: Pack ===" -ForegroundColor Cyan
& "$PSScriptRoot/build/nuget-pack.ps1"
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n=== Step 2: Publish ===" -ForegroundColor Cyan
& "$PSScriptRoot/build/nuget-publish.ps1"
