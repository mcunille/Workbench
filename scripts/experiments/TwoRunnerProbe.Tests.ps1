$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../server-partition-contract.ps1')
. (Join-Path $PSScriptRoot 'two-runner-contract.ps1')

# GIVEN four whole-class cohorts, including two case-sensitive theory identities
$names = @('Suite.A.One', 'Suite.B.One', 'Suite.C.Row(value: "a")', 'Suite.C.Row(value: "A")', 'Suite.D.One')
$partitions = @(New-ServerTestPartitions -TestNames $names -PartitionCount 4)
# WHEN two hosts receive their assignments
$first = @(Get-TwoRunnerProbePartitions -Partitions $partitions -HostId 1)
$second = @(Get-TwoRunnerProbePartitions -Partitions $partitions -HostId 2)
# THEN each host has two processes and together they cover every case exactly once.
if ($first.Count -ne 2 -or $second.Count -ne 2) { throw 'Each probe host must receive exactly two cohorts.' }
Assert-ServerTestPartitions -TestNames $names -Partitions @($first + $second)
Write-Host 'Two-runner probe selection passed.'

function Assert-Rejected([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch { if ($_.Exception.Message -match $Pattern) { return }; throw }
    throw "Expected rejection matching '$Pattern'."
}
# GIVEN one runner's artifact is absent WHEN validating the probe THEN it cannot claim full coverage.
Assert-Rejected { Assert-TwoRunnerProbeEvidence -Roots @('absent') -ExpectedHead ('a' * 40) } 'two host artifacts'

$root = Join-Path ([IO.Path]::GetTempPath()) "two-runner-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $root | Out-Null
try {
    $roots = @(1..2 | ForEach-Object { Join-Path $root "host-$_" })
    foreach ($hostId in 1..2) {
        $path = $roots[$hostId - 1]
        New-Item -ItemType Directory $path | Out-Null
        @{ HostId = $hostId; HeadRevision = ('a' * 40); CheckoutRevision = ('b' * 40); Succeeded = $true } | ConvertTo-Json | Set-Content (Join-Path $path 'probe.json')
        @{ Sha256 = ('c' * 64) } | ConvertTo-Json | Set-Content (Join-Path $path 'duration-dataset.json')
        $partitions | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $path 'inventory.json')
        foreach ($partition in (Get-TwoRunnerProbePartitions $partitions $hostId)) {
            $folder = Join-Path $path "partition-$($partition.Id)"
            New-Item -ItemType Directory $folder | Out-Null
            @{ ExitCode = 0; Seconds = 10 } | ConvertTo-Json | Set-Content (Join-Path $folder 'timing.json')
            $rows = ($partition.Tests | ForEach-Object { '<UnitTestResult testName="' + [Security.SecurityElement]::Escape($_) + '" outcome="Passed" />' }) -join ''
            Set-Content (Join-Path $folder 'results.trx') "<TestRun><Results>$rows</Results></TestRun>"
        }
    }
    # GIVEN independent passing artifacts WHEN aggregated THEN all five rows are counted once.
    $result = Assert-TwoRunnerProbeEvidence -Roots $roots -ExpectedHead ('a' * 40)
    if ($result.Cases -ne 5) { throw 'Probe lost a case-sensitive theory identity.' }
    # GIVEN artifacts from another source revision WHEN aggregated THEN reject stale evidence.
    Assert-Rejected { Assert-TwoRunnerProbeEvidence $roots ('d' * 40) } 'revision'
    # GIVEN the same host uploaded twice WHEN aggregated THEN reject the absent other host.
    Assert-Rejected { Assert-TwoRunnerProbeEvidence @($roots[0], $roots[0]) ('a' * 40) } 'host identities'
    # GIVEN different duration inputs WHEN aggregated THEN reject incomparable assignments.
    @{ Sha256 = ('d' * 64) } | ConvertTo-Json | Set-Content (Join-Path $roots[1] 'duration-dataset.json')
    Assert-Rejected { Assert-TwoRunnerProbeEvidence $roots ('a' * 40) } 'duration dataset'
    @{ Sha256 = ('c' * 64) } | ConvertTo-Json | Set-Content (Join-Path $roots[1] 'duration-dataset.json')
    # GIVEN changed global inventories WHEN aggregated THEN reject mismatched discovery.
    $inventoryPath = Join-Path $roots[1] 'inventory.json'
    $original = Get-Content $inventoryPath -Raw
    $original.Replace('Suite.A.One', 'Suite.A.Other') | Set-Content $inventoryPath
    Assert-Rejected { Assert-TwoRunnerProbeEvidence $roots ('a' * 40) } 'inventory'
    Set-Content $inventoryPath $original
    # GIVEN a missing result WHEN aggregated THEN no completion is inferred from timing alone.
    Remove-Item -LiteralPath (Join-Path $roots[1] 'partition-4/results.trx')
    Assert-Rejected { Assert-TwoRunnerProbeEvidence $roots ('a' * 40) } 'missing'
    Write-Host 'Two-runner probe evidence validation passed.'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force }
