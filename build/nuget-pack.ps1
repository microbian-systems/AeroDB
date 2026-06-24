#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Packs all Dali library projects into NuGet packages.
.DESCRIPTION
    Builds all library projects in Release mode and produces .nupkg files
    in the build/nupkgs/ directory.
.PARAMETER VersionSuffix
    Optional SemVer 2.0 suffix (e.g. "alpha.1", "rc.1").
    When set, packages are versioned as <base-version>-<suffix>.
    Default: "alpha" (produces 0.0.6-alpha).
.PARAMETER OutputDir
    Output directory for nupkg files. Default: build/nupkgs.
.PARAMETER Configuration
    Build configuration. Default: Release.
.EXAMPLE
    ./build/nuget-pack.ps1
    Packs all libraries with version 0.0.6-alpha.
    ./build/nuget-pack.ps1 -VersionSuffix "rc.1"
    Packs with version 0.0.6-rc.1.
#>

param(
    [string]$VersionSuffix = "alpha",
    [string]$OutputDir = "",
    [string]$Configuration = "Release"
)

$RepoRoot = Resolve-Path "$PSScriptRoot/.."
$OutputDir = $(if ($OutputDir) { $OutputDir } else { "$RepoRoot/build/nupkgs" })

Write-Host "=== Dali NuGet Pack Script ===" -ForegroundColor Cyan
Write-Host "Repo:     $RepoRoot" -ForegroundColor Gray
Write-Host "Output:   $OutputDir" -ForegroundColor Gray
Write-Host "Config:   $Configuration" -ForegroundColor Gray
Write-Host "Suffix:   $($VersionSuffix -replace '^', '-' -replace '^-$', '(none)')" -ForegroundColor Gray

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$libProjects = @(
    "$RepoRoot/src/Dali"
    "$RepoRoot/src/Dali.EntityFrameworkCore"
    "$RepoRoot/src/Dali.ML"
    "$RepoRoot/src/Dali.SourceGenerators"
    "$RepoRoot/src/Dali.WolverineFx"
)

$versionArgs = @()
if ($VersionSuffix) {
    $versionArgs += "-p:VersionSuffix=$VersionSuffix"
}

$failed = @()

foreach ($proj in $libProjects) {
    $csproj = Get-ChildItem "$proj/*.csproj" | Select-Object -First 1 -ExpandProperty FullName
    if (-not $csproj) {
        Write-Host "WARN: Project not found, skipping: $proj" -ForegroundColor Yellow
        continue
    }

    $projName = (Get-Item $csproj).BaseName
    Write-Host "  Packing: $projName..." -ForegroundColor Cyan
    $output = dotnet pack $csproj -c $Configuration -o $OutputDir --include-symbols -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg @versionArgs 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAILED: $(Split-Path $proj -Leaf)" -ForegroundColor Red
        $failed += $proj
        $output | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkRed }
    }
}

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
$count = (Get-ChildItem "$OutputDir/*.nupkg" -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*.snupkg' }).Count
Write-Host "Packages created: $count" -ForegroundColor Green
Write-Host "Location: $OutputDir" -ForegroundColor Green

if ($failed.Count -gt 0) {
    Write-Host "Failed: $($failed.Count)" -ForegroundColor Red
    exit 1
}
