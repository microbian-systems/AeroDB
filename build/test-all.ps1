[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $repositoryRoot 'TestResults'
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$runResults = Join-Path $ResultsDirectory ([Guid]::NewGuid().ToString('N'))

$projects = @(Get-ChildItem (Join-Path $repositoryRoot 'tests') -Recurse -File |
    Where-Object { $_.Extension -in '.csproj', '.fsproj' } |
    Sort-Object FullName)
if ($projects.Count -eq 0) { throw 'No AeroDB test projects found.' }

$summary = [Collections.Generic.List[string]]::new()
$summary.Add('| Suite | Passed | Failed | Skipped | Exit code |')
$summary.Add('|---|---:|---:|---:|---:|')
$failedSuites = 0
foreach ($project in $projects) {
    $suiteName = $project.BaseName
    $suiteResults = Join-Path $runResults $suiteName
    New-Item -ItemType Directory -Path $suiteResults -Force | Out-Null
    Write-Host "Running $suiteName"
    $arguments = @('run', '--project', $project.FullName, '--configuration', $Configuration)
    if ($NoBuild) { $arguments += '--no-build' }
    $arguments += @('--', '--reflection', '--report-trx', '--results-directory', $suiteResults)
    & dotnet @arguments
    $suiteExitCode = $LASTEXITCODE

    $report = Get-ChildItem $suiteResults -Filter '*.trx' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $passed = $failed = $skipped = 0
    if ($report) {
        [xml]$xml = Get-Content -LiteralPath $report.FullName -Raw
        $counters = $xml.TestRun.ResultSummary.Counters
        $passed = [int]$counters.passed
        $failed = [int]$counters.failed + [int]$counters.error + [int]$counters.timeout + [int]$counters.aborted
        $skipped = [int]$counters.notExecuted + [int]$counters.notRunnable
        if ([int]$counters.executed -eq 0 -or $failed -gt 0) { $suiteExitCode = 1 }
    }
    else {
        Write-Warning "$suiteName produced no TRX report; discovery/build/runtime may have failed."
        $suiteExitCode = 1
    }
    if ($suiteExitCode -ne 0) { $failedSuites++ }
    $summary.Add("| $suiteName | $passed | $failed | $skipped | $suiteExitCode |")
}

$summary.Add('')
$summary.Add("$($projects.Count) suites attempted; $failedSuites failed. Skips are reported explicitly, not counted as passes.")
$summaryText = $summary -join [Environment]::NewLine
Write-Host $summaryText
$summaryPath = Join-Path $ResultsDirectory 'test-suite-summary.md'
[IO.File]::WriteAllText($summaryPath, $summaryText)
if ($env:GITHUB_STEP_SUMMARY) {
    [IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $summaryText + [Environment]::NewLine)
}
exit $(if ($failedSuites -gt 0) { 1 } else { 0 })
