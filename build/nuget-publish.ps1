#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Pushes all .nupkg and .snupkg files from build/nupkgs/ to nuget.org.
.DESCRIPTION
    Pushes packages to the NuGet.org gallery. Requires a NuGet API key.
    The key can be provided via the -ApiKey parameter, or via the
    GITHUB_API_KEY_DALI or NUGET_API_KEY environment variable (for local publishing).
.PARAMETER ApiKey
    NuGet API key to use for publishing.
    If not provided, falls back to GITHUB_API_KEY_DALI, then NUGET_API_KEY.
.PARAMETER SkipSnupkg
    Skip pushing symbol packages (.snupkg). Default: false.
.EXAMPLE
    # Local: uses $env:GITHUB_API_KEY_DALI or $env:NUGET_API_KEY
    ./build/nuget-publish.ps1

    # CI (Trusted Publishing): pass OIDC temp key
    ./build/nuget-publish.ps1 -ApiKey "${{ steps.login.outputs.NUGET_API_KEY }}"
#>

param(
    [string]$ApiKey,
    [switch]$SkipSnupkg
)

$RepoRoot = Resolve-Path "$PSScriptRoot/.."

# --- Resolve API key: explicit param takes priority, then env var ---
if ($ApiKey) {
    Write-Host "Using provided -ApiKey parameter." -ForegroundColor Gray
} elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_API_KEY_DALI)) {
    $ApiKey = $env:GITHUB_API_KEY_DALI
    Write-Host "Using GITHUB_API_KEY_DALI environment variable." -ForegroundColor Gray
} elseif (-not [string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
    $ApiKey = $env:NUGET_API_KEY
    Write-Host "Using NUGET_API_KEY environment variable." -ForegroundColor Gray
} else {
    Write-Host "No API key provided. Set GITHUB_API_KEY_DALI or NUGET_API_KEY, or pass -ApiKey." -ForegroundColor Red
    exit 1
}

$nupkgs = Get-ChildItem "$RepoRoot/build/nupkgs/*.nupkg" -ErrorAction SilentlyContinue
if (-not $nupkgs) {
    Write-Host "No .nupkg files found in build/nupkgs/. Run ./build/nuget-pack.ps1 first." -ForegroundColor Yellow
    exit 1
}

# --- Push primary packages ---
$failed = 0
Write-Host "Pushing $($nupkgs.Count) packages to nuget.org..." -ForegroundColor Cyan
foreach ($nupkg in $nupkgs) {
    Write-Host "  $($nupkg.Name)..." -ForegroundColor Gray
    dotnet nuget push $nupkg.FullName --source https://api.nuget.org/v3/index.json --api-key "$ApiKey" --skip-duplicate
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAILED: $($nupkg.Name)" -ForegroundColor Red
        $failed++
    }
}

# --- Push symbol packages ---
if (-not $SkipSnupkg) {
    $snupkgs = Get-ChildItem "$RepoRoot/build/nupkgs/*.snupkg" -ErrorAction SilentlyContinue
    if ($snupkgs) {
        Write-Host "Pushing $($snupkgs.Count) symbol packages..." -ForegroundColor Cyan
        foreach ($snupkg in $snupkgs) {
            Write-Host "  $($snupkg.Name)..." -ForegroundColor Gray
            dotnet nuget push $snupkg.FullName --source https://api.nuget.org/v3/index.json --api-key "$ApiKey" --skip-duplicate
            if ($LASTEXITCODE -ne 0) {
                Write-Host "  FAILED: $($snupkg.Name)" -ForegroundColor Red
                $failed++
            }
        }
    }
}

if ($failed -eq 0) {
    Write-Host "All packages published successfully." -ForegroundColor Green
} else {
    Write-Host "$failed package(s) failed." -ForegroundColor Red
    exit 1
}
