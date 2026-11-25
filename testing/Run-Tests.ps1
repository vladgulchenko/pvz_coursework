param(
    [string]$PackageCache,
    [switch]$SkipRestore,
    [switch]$ExportEvidence
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $projectRoot ('TestResults\' + $runId)
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$steps = [System.Collections.Generic.List[object]]::new()
$startedAt = [DateTime]::UtcNow.ToString('o')

function Invoke-DotnetStep {
    param([string]$Name, [string[]]$CommandArguments)
    Write-Host ('Running ' + $Name + '...')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& dotnet @CommandArguments 2>&1 | ForEach-Object { $_.ToString() })
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    $watch.Stop()
    $logPath = Join-Path $runRoot ($Name + '.log')
    $lines | Set-Content -LiteralPath $logPath -Encoding UTF8
    $steps.Add([pscustomobject]@{
        name = $Name; command = 'dotnet ' + ($CommandArguments -join ' ')
        exit_code = $code; seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
    })
    $steps | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'steps.json') -Encoding UTF8
    if ($code -ne 0) {
        $lines | Select-Object -Last 35 | Write-Host
        throw "$Name failed with exit code $code. Log: $logPath"
    }
    Write-Host ($Name + ': PASS')
}

function Get-SourceManifest {
    $allowed = @('.cs', '.xaml', '.csproj', '.props', '.json', '.sln', '.manifest', '.ps1')
    foreach ($file in (Get-ChildItem -LiteralPath $projectRoot -Recurse -File | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($projectRoot.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj|\.git|\.vs|TestResults|\.local)/' -or $relative.StartsWith('testing/evidence/')) { continue }
        if ($allowed -notcontains $file.Extension) { continue }
        [pscustomobject]@{path=$relative; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
}

$oldArtifacts = [Environment]::GetEnvironmentVariable('PVZ_TEST_ARTIFACTS', 'Process')
$oldCliHome = [Environment]::GetEnvironmentVariable('DOTNET_CLI_HOME', 'Process')
$oldTelemetry = [Environment]::GetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', 'Process')
Push-Location $projectRoot
try {
    if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) {
        $env:DOTNET_CLI_HOME = Join-Path ([IO.Path]::GetTempPath()) 'PvzTestRunner'
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:PVZ_TEST_ARTIFACTS = Join-Path $runRoot 'screenshots'
    $baseCommit = 'archive-without-git'
    $sourceState = 'not-available'
    if (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) {
        $baseCommit = (& git rev-parse HEAD).Trim()
        $sourceState = if (@(& git status --porcelain).Count -eq 0) { 'clean' } else { 'dirty; see source-manifest.csv' }
    }
    $manifest = @(Get-SourceManifest)
    $manifest | Export-Csv -LiteralPath (Join-Path $runRoot 'source-manifest.csv') -NoTypeInformation -Encoding UTF8

    if (-not $SkipRestore) {
        $restore = @('--locked-mode')
        if (-not [string]::IsNullOrWhiteSpace($PackageCache)) { $restore += @('--packages', $PackageCache) }
        Invoke-DotnetStep 'restore-solution' (@('restore', 'Pvz.sln') + $restore)
        Invoke-DotnetStep 'restore-wpf' (@('restore', 'tools/Pvz.Smoke/Pvz.Smoke.csproj') + $restore)
    }
    Invoke-DotnetStep 'build-solution' @('build', 'Pvz.sln', '-c', 'Release', '--no-restore', '-warnaserror')
    Invoke-DotnetStep 'build-wpf' @('build', 'tools/Pvz.Smoke/Pvz.Smoke.csproj', '-c', 'Release', '--no-restore', '-warnaserror')
    Invoke-DotnetStep 'sqlite-tests' @('test', 'tests/Pvz.Tests/Pvz.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
        '--logger', 'trx;LogFileName=sqlite.trx', '--results-directory', $runRoot)

    $smoke = 'tools/Pvz.Smoke/bin/Release/net8.0-windows/Pvz.Smoke.dll'
    Invoke-DotnetStep 'wpf' @($smoke)
    Invoke-DotnetStep 'startup' @($smoke, '--startup')
    Invoke-DotnetStep 'restart' @($smoke, '--restart')
    Invoke-DotnetStep 'instance-lock' @($smoke, '--instance-lock')

    [xml]$trx = Get-Content -Raw -LiteralPath (Join-Path $runRoot 'sqlite.trx') -Encoding UTF8
    $counters = $trx.TestRun.ResultSummary.Counters
    $testRows = @($trx.TestRun.Results.UnitTestResult | Sort-Object testName | ForEach-Object {
        [pscustomobject]@{test=$_.testName; outcome=$_.outcome; duration=$_.duration}
    })
    $testRows | Export-Csv -LiteralPath (Join-Path $runRoot 'test-cases.csv') -NoTypeInformation -Encoding UTF8
    $groups = @('wpf', 'startup', 'restart', 'instance-lock') | ForEach-Object {
        $content = Get-Content -Raw -LiteralPath (Join-Path $runRoot ($_ + '.log')) -Encoding UTF8
        $match = [regex]::Match($content, 'PASS, (\d+) checks')
        if (-not $match.Success) { throw "No check count in $_ log." }
        [pscustomobject]@{name=$_; passed=[int]$match.Groups[1].Value; failed=0}
    }
    $summary = [ordered]@{
        started_utc=$startedAt; finished_utc=[DateTime]::UtcNow.ToString('o')
        code_commit=$baseCommit; source_state=$sourceState
        os=[Environment]::OSVersion.VersionString; sdk=(& dotnet --version).Trim()
        sqlite=[ordered]@{total=[int]$counters.total; passed=[int]$counters.passed; failed=[int]$counters.failed; skipped=[int]$counters.notExecuted}
        wpf=$groups
        native_message_boxes='Not exercised: confirmations are substituted in the WPF lifecycle probe.'
        manual_verification='Not performed; user confirmed this on 2026-11-25.'
        steps=$steps.ToArray()
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding UTF8
    @(& dotnet --info) | Set-Content -LiteralPath (Join-Path $runRoot 'environment.log') -Encoding UTF8

    if ($ExportEvidence) {
        $evidence = Join-Path $PSScriptRoot 'evidence'
        New-Item -ItemType Directory -Path $evidence -Force | Out-Null
        foreach ($name in @('summary.json', 'test-cases.csv', 'source-manifest.csv', 'wpf.log', 'startup.log', 'restart.log', 'instance-lock.log')) {
            Copy-Item -LiteralPath (Join-Path $runRoot $name) -Destination (Join-Path $evidence $name) -Force
        }
        $screenshots = Join-Path $evidence 'screenshots'
        New-Item -ItemType Directory -Path $screenshots -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $runRoot 'screenshots') -Filter '*.png' -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $screenshots $_.Name) -Force
        }
    }
    Write-Host "PASS: $($counters.passed)/$($counters.total) SQLite tests. Evidence: $runRoot"
}
finally {
    [Environment]::SetEnvironmentVariable('PVZ_TEST_ARTIFACTS', $oldArtifacts, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_HOME', $oldCliHome, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', $oldTelemetry, 'Process')
    Pop-Location
}
