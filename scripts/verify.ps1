[CmdletBinding()]
param(
    [switch]$SkipDependencyInstall,
    [ValidateRange(2, 4)][int]$ServerPartitions = 2,
    [ValidateRange(1, 4)][int]$ServerConcurrency = 2
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'verification-phases.ps1')
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $repositoryRoot "artifacts/verification/$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$serverStages = [Collections.Generic.List[object]]::new()
$gateClock = [Diagnostics.Stopwatch]::StartNew()
$prerequisiteClock = [Diagnostics.Stopwatch]::StartNew()
$passed = $false
$serverJoined = $false
try {
    $prepared = Invoke-VerificationPreparation -RepositoryRoot $repositoryRoot -RunId $runId -RunRoot $runRoot -SkipDependencyInstall:$SkipDependencyInstall -OnServerReady {
        $serverStages.Add((Start-VerificationStage -Name server -RepositoryRoot $repositoryRoot `
            -Arguments @($ServerPartitions, $ServerConcurrency, (Join-Path $runRoot 'server-tests')) -Action {
                param($count, $concurrency, $results)
                & ./scripts/test-server-partitions.ps1 -PartitionCount $count -MaxConcurrency $concurrency -NoBuild -ResultsDirectory $results
                if (-not $?) { throw 'Server partitions failed.' }
            }))
    }
    $prerequisiteClock.Stop()
    $null = Invoke-VerificationRelease -RepositoryRoot $repositoryRoot -RunId $runId -ManifestPath $prepared.LocalManifestPath -RunRoot $runRoot
    $serverJoined = $true
    Complete-VerificationStages -Stages $serverStages.ToArray() -RequiredNames @('server') -TimingPath (Join-Path $runRoot 'server.json')
    $passed = $true
    Write-Host 'Workbench source and published release-unit verification passed.'
}
finally {
    try {
        if (-not $serverJoined -and $serverStages.Count) {
            Complete-VerificationStages -Stages $serverStages.ToArray() -RequiredNames @('server') -TimingPath (Join-Path $runRoot 'server.json')
        }
    }
    finally {
        foreach ($stage in $serverStages) { Remove-Job -Job $stage.Job -Force -ErrorAction SilentlyContinue }
        $receipts = @(foreach ($name in @('client','server','release')) {
            $path = Join-Path $runRoot "$name.json"
            if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
        })
        if ($passed -and (@($receipts.Stage | Sort-Object -Unique).Count -ne 4 -or @($receipts | Where-Object { $_.Succeeded -ne $true }).Count)) { $passed=$false; throw 'Full verification receipt set is incomplete.' }
        $receipts | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'stages.json')
        [pscustomobject]@{ RunId=$runId; Succeeded=$passed; Seconds=$gateClock.Elapsed.TotalSeconds;
            PrerequisiteSeconds=$prerequisiteClock.Elapsed.TotalSeconds; ServerPartitions=$ServerPartitions;
            ServerConcurrency=$ServerConcurrency; ProcessorCount=[Environment]::ProcessorCount } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'gate.json')
        Write-Host ("Gate evidence: {0}; elapsed {1:N2}s" -f $runRoot, $gateClock.Elapsed.TotalSeconds)
    }
}
