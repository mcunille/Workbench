$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../../scripts/server-partition-contract.ps1"

function Assert-Rejected([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch { if ($_.Exception.Message -match $Pattern) { return }; throw }
    throw "Expected rejection matching '$Pattern'."
}

# GIVEN four whole classes and two theory identities differing only by case
$names = @('Suite.A.One', 'Suite.B.One', 'Suite.C.Row(value: "a")', 'Suite.C.Row(value: "A")', 'Suite.D.One')
$partitions = @(New-ServerTestPartitions $names 4)
# WHEN hosts select their cohorts THEN each receives exactly its requested cohorts.
if (((Get-ServerPartitionSelection $partitions @(1, 2)).Id -join ',') -cne '1,2' -or
    ((Get-ServerPartitionSelection $partitions @(3, 4)).Id -join ',') -cne '3,4' -or
    @(Get-ServerPartitionSelection $partitions).Count -ne 4) { throw 'Unexpected selection.' }
Assert-Rejected { Get-ServerPartitionSelection $partitions @(1, 1) } 'distinct'
Assert-Rejected { Get-ServerPartitionSelection $partitions @(5) } 'known'
Assert-Rejected { Get-ServerPartitionSelection $partitions @() } 'known'
# GIVEN canonically equivalent but distinct test identities
# WHEN a result substitutes the decomposed spelling THEN coverage rejects the substitution.
$composed = "Suite.A.Row(value: '$([char]0x00e9)')"
$decomposed = "Suite.A.Row(value: 'e$([char]0x0301)')"
Assert-Rejected { Assert-ServerTestCoverage @($composed) @($decomposed) } 'coverage mismatch'

$root = Join-Path ([IO.Path]::GetTempPath()) "distributed-server-$([guid]::NewGuid().ToString('N'))"
$source = Join-Path $root 'source'
$revision = 'a' * 40
$sourceHash = 'b' * 64
$dataset = 'c' * 64
$stages = @('client', 'browser', 'published')
$project = 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj'
function Write-Json($Value, [string]$Path) { ConvertTo-Json -InputObject $Value -Depth 8 | Set-Content -LiteralPath $Path }
try {
    $sourceId = [guid]::NewGuid().ToString('N')
    $source = Join-Path $source $sourceId
    New-Item -ItemType Directory $source -Force | Out-Null
    $sourceGate = Join-Path $source 'gate.json'
    Write-Json @{ Version = 2; RunId = $sourceId; StageGroup = 'SourceRelease'; RequiredStages = $stages; Succeeded = $true; CheckoutRevision = $revision; SourceHash = $sourceHash } $sourceGate
    Write-Json @($stages | ForEach-Object { @{ Stage = $_; State = 'Completed'; Succeeded = $true } }) (Join-Path $source 'stages.json')
    $roots = @(1..2 | ForEach-Object { Join-Path "$root/host-$_" ([guid]::NewGuid().ToString('N')) })
    $receipts = @()
    foreach ($hostIndex in 0, 1) {
        $path = $roots[$hostIndex]
        New-Item -ItemType Directory $path -Force | Out-Null
        $ids = @((1 + 2 * $hostIndex), (2 + 2 * $hostIndex))
        $receipt = @{ Version = 1; RunId = Split-Path $path -Leaf; ExecutionKind = 'Partial'; Succeeded = $true; PartitionCount = 4; PartitionIds = $ids; CheckoutRevision = $revision; SourceHash = $sourceHash; DurationSha256 = $dataset; TestProjects = @($project) }
        $receipts += $receipt
        Write-Json $receipt (Join-Path $path 'server-receipt.json')
        Write-Json $partitions (Join-Path $path 'inventory.json')
        Write-Json @{ Sha256 = $dataset } (Join-Path $path 'duration-dataset.json')
        foreach ($partition in (Get-ServerPartitionSelection $partitions $ids)) {
            $folder = Join-Path $path "partition-$($partition.Id)"
            New-Item -ItemType Directory $folder | Out-Null
            Write-Json @{ ExitCode = 0; Seconds = 10 } (Join-Path $folder 'timing.json')
            $rows = ($partition.Tests | ForEach-Object { '<UnitTestResult testName="' + [Security.SecurityElement]::Escape($_) + '" outcome="Passed" />' }) -join ''
            Set-Content (Join-Path $folder 'results.trx') "<TestRun><Results>$rows</Results></TestRun>"
        }
    }
    # GIVEN complete current evidence WHEN it is joined THEN every literal case counts once.
    $result = Assert-DistributedServerVerification $sourceGate $roots $revision $sourceHash $dataset
    if ($result.Cases -ne 5 -or ($result.PartitionIds -join ',') -cne '1,2,3,4') { throw 'Unexpected aggregate.' }
    Assert-Rejected { Assert-DistributedServerVerification $sourceGate @($roots[0]) $revision $sourceHash $dataset } 'two host'
    foreach ($change in @(
        @{ Key = 'PartitionIds'; Value = @(1, 2); Error = 'cohort assignment' },
        @{ Key = 'CheckoutRevision'; Value = 'd' * 40; Error = 'source identity' },
        @{ Key = 'SourceHash'; Value = 'd' * 64; Error = 'source identity' },
        @{ Key = 'DurationSha256'; Value = 'd' * 64; Error = 'duration dataset' },
        @{ Key = 'TestProjects'; Value = @($project, 'new-tests.csproj'); Error = 'test project' }
    )) {
        # GIVEN an independently corrupted receipt WHEN joined THEN it cannot satisfy the gate.
        $mutant = $receipts[1].Clone()
        $mutant[$change.Key] = $change.Value
        $path = Join-Path $roots[1] 'server-receipt.json'
        Write-Json $mutant $path
        Assert-Rejected { Assert-DistributedServerVerification $sourceGate $roots $revision $sourceHash $dataset } $change.Error
        Write-Json $receipts[1] $path
    }
    $inventoryPath = Join-Path $roots[1] 'inventory.json'
    $original = Get-Content -LiteralPath $inventoryPath -Raw
    Set-Content -LiteralPath $inventoryPath $original.Replace('Suite.A.One', 'Suite.A.Other')
    Assert-Rejected { Assert-DistributedServerVerification $sourceGate $roots $revision $sourceHash $dataset } 'inventory'
    Set-Content -LiteralPath $inventoryPath $original
    $timingPath = Join-Path $roots[1] 'partition-4/timing.json'
    Write-Json @{} $timingPath
    Assert-Rejected { Assert-DistributedServerVerification $sourceGate $roots $revision $sourceHash $dataset } 'native completion'
    # GIVEN complete receipts WHEN the cohort exits nonzero THEN successful TRX rows cannot make it pass.
    Write-Json @{ ExitCode = 1; Seconds = 10 } $timingPath
    Assert-Rejected { Assert-DistributedServerVerification $sourceGate $roots $revision $sourceHash $dataset } 'failed with exit code 1'
    # GIVEN successful native completion WHEN the TRX has a failing row THEN the aggregate rejects it.
    Write-Json @{ ExitCode = 0; Seconds = 10 } $timingPath
    $trxPath = Join-Path $roots[1] 'partition-4/results.trx'
    (Get-Content -LiteralPath $trxPath -Raw).Replace('outcome="Passed"', 'outcome="Failed"') | Set-Content -LiteralPath $trxPath
    Assert-Rejected { Assert-DistributedServerVerification $sourceGate $roots $revision $sourceHash $dataset } 'non-passing outcome'
    Write-Host 'Distributed server admission passed.'
}
finally {
    if (Test-Path -LiteralPath $root) {
        $resolved = (Resolve-Path -LiteralPath $root).Path
        if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected fixture location.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
