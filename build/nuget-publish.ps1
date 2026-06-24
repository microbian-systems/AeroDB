#!/usr/bin/env pwsh

$RepoRoot = Resolve-Path "$PSScriptRoot/.."
$nupkgs = Get-ChildItem "$RepoRoot/build/nupkgs/*.nupkg" -ErrorAction SilentlyContinue
if (-not $nupkgs) {
    Write-Host "No .nupkg files found in build/nupkgs/. Run ./build/nuget-pack.ps1 first." -ForegroundColor Yellow
    exit 1
}

if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
    Write-Host "NUGET_API_KEY is empty or not set." -ForegroundColor Red
    Write-Host "Set it with: `$env:NUGET_API_KEY = 'your-key-here'" -ForegroundColor Yellow
    exit 1
}

Write-Host "Pushing $($nupkgs.Count) packages to nuget.org..." -ForegroundColor Cyan
$failed = 0
foreach ($nupkg in $nupkgs) {
    Write-Host "  $($nupkg.Name)..." -ForegroundColor Gray
    dotnet nuget push $nupkg.FullName --source https://api.nuget.org/v3/index.json --api-key "$env:NUGET_API_KEY" --skip-duplicate
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAILED: $($nupkg.Name)" -ForegroundColor Red
        $failed++
    }
}

# Push symbol packages (.snupkg)
$snupkgs = Get-ChildItem "$RepoRoot/build/nupkgs/*.snupkg" -ErrorAction SilentlyContinue
if ($snupkgs) {
    Write-Host "Pushing $($snupkgs.Count) symbol packages..." -ForegroundColor Cyan
    foreach ($snupkg in $snupkgs) {
        Write-Host "  $($snupkg.Name)..." -ForegroundColor Gray
        dotnet nuget push $snupkg.FullName --source https://api.nuget.org/v3/index.json --api-key "$env:NUGET_API_KEY" --skip-duplicate
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  FAILED: $($snupkg.Name)" -ForegroundColor Red
            $failed++
        }
    }
}

if ($failed -eq 0) {
    Write-Host "All packages published successfully." -ForegroundColor Green
} else {
    Write-Host "$failed package(s) failed." -ForegroundColor Red
    exit 1
}
