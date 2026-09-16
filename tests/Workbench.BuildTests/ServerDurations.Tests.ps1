$ErrorActionPreference = 'Stop'
$importer = Join-Path $PSScriptRoot '../../scripts/update-server-test-durations.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "server-durations-$([Guid]::NewGuid().ToString('N'))"
$evidence = Join-Path $testRoot 'gate'
$server = Join-Path $evidence 'server-tests/0123456789abcdef0123456789abcdef'
$partition = Join-Path $server 'partition-1'
$output = Join-Path $testRoot 'durations.json'
$runUrl = 'https://github.com/mcunille/Workbench/actions/runs/123456'
$revision = '0123456789abcdef0123456789abcdef01234567'
New-Item -ItemType Directory $partition -Force | Out-Null
function Reset-Evidence {
    @{ Succeeded = $true; ServerPartitions = 1 } | ConvertTo-Json | Set-Content (Join-Path $evidence 'gate.json')
    ConvertTo-Json -InputObject @(@{ Id = 1; Tests = @('Suite.z', 'Suite.A(value: "é漢字")', 'Suite.a') }) -Depth 5 | Set-Content (Join-Path $server 'inventory.json')
    @{ ExitCode = 0; Seconds = 2 } | ConvertTo-Json | Set-Content (Join-Path $partition 'timing.json')
    Set-Content (Join-Path $partition 'results.trx') '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="Suite.z" duration="00:00:01.2500000" outcome="Passed"/><UnitTestResult testName="Suite.A(value: &quot;é漢字&quot;)" duration="00:00:00" outcome="Passed"/><UnitTestResult testName="Suite.a" duration="00:00:00.0000100" outcome="Passed"/></Results></TestRun>'
}
function Import-Evidence {
    & $importer -EvidenceDirectory $evidence -SourceRunUrl $runUrl -SourceRevision $revision -OutputPath $output
}
function Assert-Rejected([scriptblock]$Change, [string]$Pattern) {
    Reset-Evidence
    & $Change
    Set-Content $output 'unchanged'
    $rejected = $false
    try { Import-Evidence } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; $rejected = $true }
    if (-not $rejected) { throw "Expected rejection: $Pattern" }
    if ((Get-Content $output -Raw).Trim() -cne 'unchanged') { throw 'Invalid evidence changed the timing baseline.' }
}
try {
    # GIVEN completed successful evidence with exact Unicode names and zero/sub-millisecond durations
    Reset-Evidence
    # WHEN the data-only importer creates a timing baseline
    Import-Evidence
    $baseline = Get-Content $output -Raw | ConvertFrom-Json -AsHashtable
    # THEN provenance and ordinal test identity are preserved, and only zero duration is clamped.
    if ($baseline.schemaVersion -ne 1 -or $baseline.sourceRunUrl -cne $runUrl -or $baseline.sourceRevision -cne $revision -or $baseline.fallbackSeconds -ne 1) { throw 'Baseline metadata is incorrect.' }
    if (($baseline.durations.Keys -join '|') -cne 'Suite.A(value: "é漢字")|Suite.a|Suite.z') { throw 'Duration names are not sorted ordinally.' }
    if ($baseline.durations['Suite.z'] -ne 1.25 -or $baseline.durations['Suite.A(value: "é漢字")'] -ne .001 -or $baseline.durations['Suite.a'] -ne .00001) { throw 'Durations were not imported faithfully.' }
    # GIVEN an uppercase immutable revision WHEN imported THEN provenance uses canonical lowercase hex.
    $revision = $revision.ToUpperInvariant()
    Import-Evidence
    if ((Get-Content $output -Raw | ConvertFrom-Json).sourceRevision -cne $revision.ToLowerInvariant()) { throw 'Revision is not canonical lowercase.' }
    # GIVEN invalid gate, process, inventory, result, duration or XML evidence
    # WHEN importing THEN the operation fails without replacing a previously trusted baseline.
    Assert-Rejected { Set-Content (Join-Path $evidence 'gate.json') '{"Succeeded":false,"ServerPartitions":1}' } 'successful'
    Assert-Rejected { Set-Content (Join-Path $evidence 'gate.json') '{"Succeeded":"true","ServerPartitions":1}' } 'successful'
    Assert-Rejected { Set-Content (Join-Path $evidence 'gate.json') '{"Succeeded":true,"ServerPartitions":2}' } 'partition'
    Assert-Rejected { Set-Content (Join-Path $partition 'timing.json') '{"ExitCode":1}' } 'exit code'
    Assert-Rejected { Set-Content (Join-Path $partition 'timing.json') '{}' } 'exit code'
    Assert-Rejected { Set-Content (Join-Path $server 'inventory.json') '[{"Id":1,"Tests":["Suite.z","Suite.z"]}]' } 'duplicate'
    Assert-Rejected { (Get-Content (Join-Path $partition 'results.trx') -Raw).Replace('Passed','NotExecuted') | Set-Content (Join-Path $partition 'results.trx') } 'outcome'
    Assert-Rejected { (Get-Content (Join-Path $partition 'results.trx') -Raw).Replace('Suite.z','Unknown.z') | Set-Content (Join-Path $partition 'results.trx') } 'coverage'
    Assert-Rejected { (Get-Content (Join-Path $partition 'results.trx') -Raw).Replace('Suite.z','Suite.a') | Set-Content (Join-Path $partition 'results.trx') } 'duplicate'
    Assert-Rejected { (Get-Content (Join-Path $partition 'results.trx') -Raw).Replace('00:00:01.2500000','-00:00:01') | Set-Content (Join-Path $partition 'results.trx') } 'duration'
    Assert-Rejected { (Get-Content (Join-Path $partition 'results.trx') -Raw).Replace('00:00:01.2500000','NaN') | Set-Content (Join-Path $partition 'results.trx') } 'duration'
    Assert-Rejected { Set-Content (Join-Path $partition 'results.trx') '<!DOCTYPE TestRun [<!ENTITY value "x">]><TestRun>&value;</TestRun>' } 'DTD'
    Assert-Rejected { Remove-Item -LiteralPath (Join-Path $partition 'results.trx') } 'Could not find|cannot find'
    $extraPartition = Join-Path $server 'partition-2'
    Assert-Rejected { New-Item -ItemType Directory $extraPartition | Out-Null } 'partition'
    Remove-Item -LiteralPath $extraPartition
    # GIVEN globally complete passing rows which ran in the wrong partitions
    # WHEN importing THEN per-partition coverage must still match the inventory.
    Assert-Rejected {
        New-Item -ItemType Directory $extraPartition | Out-Null
        Set-Content (Join-Path $evidence 'gate.json') '{"Succeeded":true,"ServerPartitions":2}'
        Set-Content (Join-Path $server 'inventory.json') '[{"Id":1,"Tests":["Suite.a"]},{"Id":2,"Tests":["Suite.z"]}]'
        Set-Content (Join-Path $partition 'results.trx') '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="Suite.z" duration="00:00:01" outcome="Passed"/></Results></TestRun>'
        Set-Content (Join-Path $extraPartition 'results.trx') '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="Suite.a" duration="00:00:01" outcome="Passed"/></Results></TestRun>'
        Set-Content (Join-Path $extraPartition 'timing.json') '{"ExitCode":0}'
    } 'coverage'
    Remove-Item -LiteralPath (Join-Path $extraPartition 'results.trx'), (Join-Path $extraPartition 'timing.json')
    Remove-Item -LiteralPath $extraPartition
    # GIVEN provenance which cannot identify a trusted repository run or immutable revision
    # WHEN importing THEN neither executable text nor loose URLs are accepted.
    $runUrl = 'https://example.org/actions/runs/123456'
    Assert-Rejected {} 'SourceRunUrl'
    $runUrl = 'https://github.com/mcunille/Workbench/actions/runs/123456'
    $revision = 'main'
    Assert-Rejected {} 'SourceRevision'
    Write-Host 'Server duration import tests passed.'
}
finally {
    if ([IO.Path]::GetFullPath($testRoot).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
