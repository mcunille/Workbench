[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts/verification-stages.ps1')
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch { if ($_.Exception.Message -match $Pattern) { return }; throw }
    throw "Expected failure: $Pattern"
}
# GIVEN all four required CI jobs
$required = @('local-setup-contracts', 'deployment-contracts', 'verify', 'container-smoke')
$needs = @{}; foreach ($name in $required) { $needs[$name] = @{ result = 'success' } }
# WHEN the aggregate checks successful results THEN it accepts every required job.
Assert-RequiredCiJobs -Needs $needs
foreach ($state in @('failure', 'cancelled', 'skipped', '', 'unknown')) {
    # GIVEN a required job that did not succeed WHEN aggregated THEN the gate fails closed.
    $needs.verify.result = $state
    Assert-Fails { Assert-RequiredCiJobs -Needs $needs } 'Required CI job'
}
$needs.verify.result = 'success'
$needs.Remove('verify')
# GIVEN a missing required job WHEN aggregated THEN success of remaining jobs is insufficient.
Assert-Fails { Assert-RequiredCiJobs -Needs $needs } 'Required CI job'
# GIVEN the workflow job graph WHEN the aggregate is scheduled THEN it runs even after dependency failure.
$workflow = Get-Content (Join-Path $root '.github/workflows/ci.yml') -Raw
if ($workflow -notmatch '(?s)required-gate:.*?if: \$\{\{ always\(\) \}\}.*?needs: \[local-setup-contracts, deployment-contracts, verify, container-smoke\]' -or
    $workflow -notmatch 'Assert-RequiredCiJobs -Needs') { throw 'Aggregate CI graph does not enforce the required jobs.' }
$jobs = @()
try {
    # GIVEN independent child processes WHEN one fails THEN others finish and failure propagates.
    $jobs += Start-VerificationStage -Name good -RepositoryRoot $root -Action { 'stage output'; }
    $jobs += Start-VerificationStage -Name bad -RepositoryRoot $root -Action { throw 'deliberate failure' }
    Assert-Fails { Complete-VerificationStages -Stages $jobs -RequiredNames @('good', 'bad') } 'bad'
    # GIVEN a stopped child and one that exits without its receipt WHEN joined THEN both fail closed.
    $jobs += Start-VerificationStage -Name stopped -RepositoryRoot $root -Action { Start-Sleep -Seconds 30 }
    Stop-Job -Job $jobs[2].Job
    Assert-Fails { Complete-VerificationStages -Stages @($jobs[2]) -RequiredNames @('stopped') } 'stopped'
    $jobs += Start-VerificationStage -Name no_receipt -RepositoryRoot $root -Action { exit 0 }
    Assert-Fails { Complete-VerificationStages -Stages @($jobs[3]) -RequiredNames @('no_receipt') } 'no_receipt'
    # GIVEN absent/duplicate required stages WHEN joined THEN completeness is enforced.
    Assert-Fails { Complete-VerificationStages -Stages @($jobs[0]) -RequiredNames @('good', 'bad') } 'missing|Missing'
    Assert-Fails { Complete-VerificationStages -Stages @($jobs[0], $jobs[0]) -RequiredNames @('good') } 'Duplicate|duplicate'
} finally { $jobs | ForEach-Object { Remove-Job -Job $_.Job -Force -ErrorAction SilentlyContinue } }
# GIVEN a first stage waiting for evidence that a later failure has been reported.
$coordinationRoot = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $coordinationRoot | Out-Null
$releasePath = Join-Path $coordinationRoot 'failure-reported'
$completionPath = Join-Path $coordinationRoot 'slow-completed'
$timingPath = Join-Path $coordinationRoot 'timings.json'
$coordinated = @()
function Write-Host {
    param([Parameter(ValueFromRemainingArguments)]$Object)
    if (($Object -join ' ') -match 'fast_failure: success=False') {
        Set-Content -LiteralPath $releasePath -Value 'reported'
    }
    Microsoft.PowerShell.Utility\Write-Host @Object
}
try {
    $coordinated += Start-VerificationStage -Name slow_first -RepositoryRoot $root -Arguments @($releasePath, $completionPath) -Action {
        param($release, $completed)
        $watchdog = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $release)) {
            if ($watchdog.Elapsed.TotalSeconds -gt 10) { throw 'Later failure was hidden behind the first stage.' }
            Start-Sleep -Milliseconds 20
        }
        Set-Content -LiteralPath $completed -Value 'completed'
    }
    $coordinated += Start-VerificationStage -Name fast_failure -RepositoryRoot $root -Action { throw 'deliberate coordinated failure' }
    # WHEN joining THEN the failure is reported before waiting for the first stage, without abandoning it.
    Assert-Fails { Complete-VerificationStages -Stages $coordinated -RequiredNames @('slow_first', 'fast_failure') -TimingPath $timingPath } 'fast_failure'
    if (-not (Test-Path -LiteralPath $completionPath)) { throw 'Fast failure was not reported while the slow stage was still running.' }
    # AND complete timing receipts retain both the successful stage and the failed stage.
    $timings = @(Get-Content -LiteralPath $timingPath -Raw | ConvertFrom-Json)
    if ($timings.Count -ne 2 -or ($timings | Where-Object Stage -eq 'slow_first').Succeeded -ne $true -or
        ($timings | Where-Object Stage -eq 'fast_failure').Succeeded -ne $false -or
        @($coordinated | Where-Object { $_.Job.State -ne 'Completed' }).Count) { throw 'Joining did not preserve all stage completion evidence.' }
}
finally {
    Remove-Item Function:\Write-Host
    $coordinated | ForEach-Object { Remove-Job -Job $_.Job -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $releasePath, $completionPath, $timingPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $coordinationRoot -Force
}
Write-Host 'Verification stage and aggregate failure contracts passed.'
