$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/server-partition-contract.ps1')

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action } catch { if ($_.Exception.Message -match $Message) { return }; throw }
    throw "Expected rejection matching '$Message'."
}

# GIVEN a Windows-style legacy console encoding and a native producer writing UTF-8 bytes
$encodingTestRoot = Join-Path ([IO.Path]::GetTempPath()) "partition-encoding-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $encodingTestRoot | Out-Null
$savedConsoleEncoding = [Console]::OutputEncoding
$savedOutputEncoding = $OutputEncoding
try {
    [Console]::OutputEncoding = [Text.Encoding]::GetEncoding(437)
    $OutputEncoding = [Text.Encoding]::ASCII
    $expectedNativeName = 'Suite.Theory(value: "···é漢字")'
    $nativeBytes = [Text.Encoding]::UTF8.GetBytes("The following Tests are available:`n    $expectedNativeName`n")
    $nativeScript = Join-Path $encodingTestRoot 'emit-native-utf8.ps1'
    Set-Content $nativeScript ('[Console]::OpenStandardOutput().Write([Convert]::FromBase64String(''' + [Convert]::ToBase64String($nativeBytes) + '''))')
    # WHEN discovery captures bytes from a real separate process
    $decodedNames = @(Invoke-ServerTestDiscovery -Command { & (Join-Path $PSHOME 'pwsh') -NoProfile -File $nativeScript })
    # THEN non-ASCII theory names stay exact and both caller encodings are preserved.
    if ($decodedNames.Count -ne 1 -or $decodedNames[0] -cne $expectedNativeName) { throw 'Native UTF-8 test inventory was decoded incorrectly.' }
    if ([Console]::OutputEncoding.CodePage -ne 437 -or $OutputEncoding.CodePage -ne 20127) { throw 'Discovery changed caller encodings.' }
    # GIVEN valid discovery rows whose custom display names contain leading/trailing whitespace
    $customNames = @('Suite.Normal', ' leading-space', 'Suite.Trailing ', ' ')
    $nativeBytes = [Text.Encoding]::UTF8.GetBytes("The following Tests are available:`n" + (($customNames | ForEach-Object { "    $_" }) -join "`n") + "`n")
    Set-Content $nativeScript ('[Console]::OpenStandardOutput().Write([Convert]::FromBase64String(''' + [Convert]::ToBase64String($nativeBytes) + '''))')
    # WHEN discovery removes only VSTest's four-space prefix THEN no test identity is dropped or trimmed.
    $customActual = @(Invoke-ServerTestDiscovery -Command { & (Join-Path $PSHOME 'pwsh') -NoProfile -File $nativeScript })
    Assert-ServerTestCoverage -Expected $customNames -Actual $customActual
    # AND unsupported custom names fail the partition contract instead of shrinking its inventory.
    Assert-Rejected { New-ServerTestPartitions -TestNames $customActual -PartitionCount 2 } 'Unsupported discovered'
    Assert-Rejected { Invoke-ServerTestDiscovery -Command { throw 'discovery command failed' } } 'discovery command failed'
    if ([Console]::OutputEncoding.CodePage -ne 437 -or $OutputEncoding.CodePage -ne 20127) { throw 'Failed discovery changed caller encodings.' }
}
finally {
    [Console]::OutputEncoding = $savedConsoleEncoding
    $OutputEncoding = $savedOutputEncoding
    Remove-Item -LiteralPath $encodingTestRoot -Recurse -Force
}

# GIVEN discovered facts and multiple rows of the same theory
Assert-ServerTestProjects -TestProjects @('server.csproj') -SupportedProject 'server.csproj'
Assert-Rejected { Assert-ServerTestProjects -TestProjects @('server.csproj', 'new-tests.csproj') -SupportedProject 'server.csproj' } 'project inventory changed'
Assert-Rejected { Assert-ServerTestProjects -TestProjects @() -SupportedProject 'server.csproj' } 'project inventory changed'
$inventory = @('Suite.A.First', 'Suite.A.Theory(value: 1)', 'Suite.A.Theory(value: 2)', 'Suite.B.Last', 'Suite.C.Other', 'Suite.D.Final')
# WHEN the inventory is divided between independent processes
$partitions = @(New-ServerTestPartitions -TestNames $inventory -PartitionCount 3)
# THEN every discovered row belongs to exactly one nonempty partition and theory rows stay together.
Assert-ServerTestPartitions -TestNames $inventory -Partitions $partitions
if (@($partitions | Where-Object { $_.Tests -contains $inventory[1] -and $_.Tests -contains $inventory[2] }).Count -ne 1) {
    throw 'Theory rows must use the same method filter.'
}
if (($partitions | ForEach-Object { $_.Tests.Count } | Measure-Object -Maximum).Maximum -gt 3) { throw 'Partitions are unbalanced.' }

# GIVEN incomplete, overlapping, empty or invalid discovery evidence
# WHEN its partition contract is checked
# THEN verification refuses to treat it as complete.
Assert-Rejected { New-ServerTestPartitions -TestNames @() -PartitionCount 3 } 'empty'
Assert-Rejected { New-ServerTestPartitions -TestNames @('A.One', 'A.One') -PartitionCount 2 } 'duplicate'
Assert-Rejected { New-ServerTestPartitions -TestNames @('A.One') -PartitionCount 2 } 'nonempty'
Assert-Rejected { Assert-ServerTestPartitions -TestNames $inventory -Partitions @($partitions[0], $partitions[1]) } 'coverage'
$overlap = @($partitions) + @($partitions[0])
Assert-Rejected { Assert-ServerTestPartitions -TestNames $inventory -Partitions $overlap } 'coverage'
Assert-Rejected { Assert-ServerTestPartitions -TestNames $inventory -Partitions (@($partitions) + @([pscustomobject]@{ Tests = @() })) } 'empty'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "partition-contract-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $testRoot | Out-Null
try {
    $trx = Join-Path $testRoot 'results.trx'
    function Write-TestTrx([string[]]$Names, [string]$Outcome = 'Passed') {
        $results = ($Names | ForEach-Object { '<UnitTestResult testName="' + [Security.SecurityElement]::Escape($_) + '" outcome="' + $Outcome + '" />' }) -join ''
        Set-Content $trx "<TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Results>$results</Results></TestRun>"
    }
    # GIVEN successful result evidence covering exactly the assigned inventory
    Write-TestTrx $partitions[0].Tests
    # WHEN validated THEN the partition succeeds.
    Assert-ServerPartitionResult -Partition $partitions[0] -ResultPath $trx -ExitCode 0
    # GIVEN a failed native process, absent evidence, skipped test, missing row, extra row or duplicate row
    # WHEN validated THEN each fails the gate even when the other signals report success.
    Assert-Rejected { Assert-ServerPartitionResult $partitions[0] $trx 1 } 'exit code'
    Assert-Rejected { Assert-ServerPartitionResult $partitions[0] (Join-Path $testRoot 'missing.trx') 0 } 'missing'
    Write-TestTrx $partitions[0].Tests 'NotExecuted'
    Assert-Rejected { Assert-ServerPartitionResult $partitions[0] $trx 0 } 'outcome'
    Write-TestTrx @()
    Assert-Rejected { Assert-ServerPartitionResult $partitions[0] $trx 0 } 'coverage'
    Write-TestTrx (@($partitions[0].Tests) + @('Unassigned.Test'))
    Assert-Rejected { Assert-ServerPartitionResult $partitions[0] $trx 0 } 'coverage'
    Write-TestTrx (@($partitions[0].Tests) + @($partitions[0].Tests[0]))
    Assert-Rejected { Assert-ServerPartitionResult $partitions[0] $trx 0 } 'coverage'
    # GIVEN the expected row count but a substituted test identity
    # WHEN coverage is checked THEN counts alone cannot satisfy the inventory.
    $substituted = @($partitions[0].Tests)
    $substituted[0] = 'Unassigned.Substitute'
    Write-TestTrx $substituted
    Assert-Rejected { Assert-ServerPartitionResult $partitions[0] $trx 0 } 'coverage'
}
finally { Remove-Item -LiteralPath $testRoot -Recurse -Force }

# GIVEN native/process shims that execute the same inventory and result boundaries
$global:partitionTestNames = $inventory
$global:partitionTestMode = 'success'
$global:partitionTestJobs = [Collections.Generic.List[object]]::new()
$global:partitionTestCommands = [Collections.Generic.List[string]]::new()
$global:partitionTestPeak = 0
function global:dotnet {
    $global:partitionTestCommands.Add(($args -join ' '))
    $global:LASTEXITCODE = 0
    if ($args -contains '-getProperty:IsTestProject') { if ($args[1] -match 'IntegrationTests') { 'true' } else { 'false' } }
    if ($args -contains '--list-tests') {
        'The following Tests are available:'
        foreach ($name in $global:partitionTestNames) { "    $name" }
    }
}
function global:Start-Job {
    param($ScriptBlock, $ArgumentList)
    [xml]$settings = Get-Content $ArgumentList[3] -Raw
    if ($settings.RunSettings.xUnit.ParallelizeTestCollections -cne 'false' -or
        $settings.RunSettings.xUnit.ParallelizeAssembly -cne 'false' -or
        $settings.RunSettings.xUnit.MaxParallelThreads -ne '1' -or
        $settings.RunSettings.RunConfiguration.MaxCpuCount -ne '1') { throw 'Process-wide isolation settings missing.' }
    if ($ScriptBlock.ToString() -notmatch '--no-build --no-restore') { throw 'Partitions must consume the shared build.' }
    $methods = $settings.RunSettings.RunConfiguration.TestCaseFilter.Split('|') | ForEach-Object { $_ -replace '^FullyQualifiedName=', '' }
    $names = @($global:partitionTestNames | Where-Object { ($_ -replace '\(.*$', '') -in $methods })
    $results = ($names | ForEach-Object { '<UnitTestResult testName="' + [Security.SecurityElement]::Escape($_) + '" outcome="Passed" />' }) -join ''
    if ($global:partitionTestMode -ne 'missing-trx') {
        Set-Content (Join-Path $ArgumentList[4] 'results.trx') "<TestRun><Results>$results</Results></TestRun>"
    }
    $job = [pscustomobject]@{ State = 'Running'; Output = [pscustomobject]@{ ExitCode = $(if ($global:partitionTestMode -eq 'exit-failure') { 1 } else { 0 }); Seconds = 0.1 } }
    $global:partitionTestJobs.Add($job)
    $global:partitionTestPeak = [Math]::Max($global:partitionTestPeak, @($global:partitionTestJobs | Where-Object State -eq Running).Count)
    return $job
}
function global:Wait-Job {
    param($Job, [switch]$Any, $Timeout)
    $Job[0].State = if ($global:partitionTestMode -eq 'cancelled') { 'Stopped' } else { 'Completed' }
}
function global:Receive-Job { param($Job, $ErrorAction) if ($global:partitionTestMode -ne 'missing-completion') { $Job.Output } }
function global:Stop-Job { param($Job, $ErrorAction) $Job.State = 'Stopped' }
function global:Remove-Job { param($Job, [switch]$Force, $ErrorAction) }
$runnerRoot = Join-Path ([IO.Path]::GetTempPath()) "partition-runner-$([Guid]::NewGuid().ToString('N'))"
try {
    $runner = Join-Path $PSScriptRoot '../../scripts/test-server-partitions.ps1'
    # WHEN standalone execution runs with bounded concurrency
    & $runner -ResultsDirectory $runnerRoot -MaxConcurrency 2 -PartitionCount 3
    # THEN it builds once, uses exactly three jobs, and never exceeds the configured process bound.
    if ($global:partitionTestPeak -ne 2 -or $global:partitionTestJobs.Count -ne 3 -or
        @($global:partitionTestCommands | Where-Object { $_ -match '^build ' }).Count -ne 1 -or
        @($global:partitionTestCommands | Where-Object { $_ -match '^restore .*--locked-mode' }).Count -ne 1) {
        throw 'Standalone preparation or bounded independent processes failed.'
    }
    $global:partitionTestCommands.Clear()
    # GIVEN a prepared gate invocation WHEN NoBuild runs THEN it only discovers and consumes current build outputs.
    & $runner -ResultsDirectory $runnerRoot -NoBuild -MaxConcurrency 1
    $discoveryCommands = @($global:partitionTestCommands | Where-Object { $_ -notmatch '^msbuild ' })
    if ($discoveryCommands.Count -ne 1 -or $discoveryCommands[0] -notmatch '--no-build --no-restore --list-tests') {
        throw 'Prepared invocation repeated shared build prerequisites.'
    }
    # GIVEN failure, cancellation, absent completion or absent current-run TRX
    # WHEN all required partitions are collected THEN the runner rejects the aggregate.
    foreach ($mode in @('exit-failure', 'cancelled', 'missing-completion', 'missing-trx')) {
        $global:partitionTestMode = $mode
        Assert-Rejected { & $runner -ResultsDirectory $runnerRoot -NoBuild } 'Server partitions failed'
    }
}
finally {
    foreach ($name in @('dotnet', 'Start-Job', 'Wait-Job', 'Receive-Job', 'Stop-Job', 'Remove-Job')) {
        Remove-Item "Function:\$name" -ErrorAction SilentlyContinue
    }
    Get-Variable 'partitionTest*' -Scope Global | Remove-Variable -Scope Global
    if (Test-Path $runnerRoot) { Remove-Item -LiteralPath $runnerRoot -Recurse -Force }
    $global:LASTEXITCODE = 0
}
Write-Host 'Server partition completeness and failure propagation passed.'
# GIVEN skewed measured durations and a new test without history
$skewNames = @('Suite.A', 'Suite.B', 'Suite.C', 'Suite.D', 'Suite.New')
$weights = @{ 'Suite.A' = 20.0; 'Suite.B' = 18.0; 'Suite.C' = 2.0; 'Suite.D' = 1.0 }
# WHEN whole methods are scheduled by predicted duration
$weighted = @(New-ServerTestPartitions -TestNames $skewNames -PartitionCount 2 -Durations $weights -FallbackSeconds 1)
# THEN the loads are 21 seconds each with one explicitly recorded fallback.
if (($weighted.PredictedSeconds -join ',') -ne '21,21' -or ($weighted.FallbackTests | Measure-Object -Sum).Sum -ne 1) { throw 'Duration balancing or fallback failed.' }
Assert-ServerTestPartitions -TestNames $skewNames -Partitions $weighted
# GIVEN the same inventory in reverse discovery order
# WHEN scheduled THEN assignment and output order remain byte-for-byte deterministic.
$reversed = @($skewNames); [array]::Reverse($reversed)
$again = @(New-ServerTestPartitions -TestNames $reversed -PartitionCount 2 -Durations $weights)
if (($weighted | ConvertTo-Json -Depth 6 -Compress) -cne ($again | ConvertTo-Json -Depth 6 -Compress)) { throw 'Duration assignment depends on discovery order.' }
# GIVEN a theory whose total duration exceeds each fact
# WHEN scheduled THEN its rows remain together and their weights are summed.
$theory = @(New-ServerTestPartitions -TestNames @('Suite.T(x: 2)', 'Suite.T(x: 1)', 'Suite.F', 'Suite.G') -PartitionCount 2 -Durations @{ 'Suite.T(x: 1)' = 6; 'Suite.T(x: 2)' = 6; 'Suite.F' = 10; 'Suite.G' = 2 })
if ($theory[0].PredictedSeconds -ne 12 -or $theory[0].Tests.Count -ne 2 -or $theory[1].PredictedSeconds -ne 12) { throw 'Theory duration aggregation failed.' }
# GIVEN invalid weights WHEN consumed THEN fail closed, including obsolete entries.
foreach ($bad in @(-1, 0, [double]::NaN, [double]::PositiveInfinity, '5')) {
    Assert-Rejected { New-ServerTestPartitions -TestNames $skewNames -PartitionCount 2 -Durations @{ 'Obsolete.Test' = $bad } } 'duration'
}
Assert-Rejected { New-ServerTestPartitions -TestNames $skewNames -PartitionCount 2 -FallbackSeconds 0 } 'fallback'
$datasetRoot = Join-Path ([IO.Path]::GetTempPath()) "duration-reader-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $datasetRoot | Out-Null
try {
    # GIVEN a versioned timing dataset with a case-sensitive test identity
    $datasetPath = Join-Path $datasetRoot 'weights.json'
    $dataset = @{ schemaVersion = 1; sourceRevision = ('a' * 40); sourceRunUrl = 'https://github.com/mcunille/Workbench/actions/runs/123'; fallbackSeconds = 1; durations = @{ 'Suite.Test' = 5 } }
    $dataset | ConvertTo-Json | Set-Content $datasetPath
    # WHEN loaded THEN its exact bytes are identified by SHA256 and provenance is retained.
    $loaded = Read-ServerTestDurations $datasetPath
    if ($loaded.Sha256 -cne (Get-FileHash $datasetPath -Algorithm SHA256).Hash.ToLowerInvariant() -or $loaded.SourceRevision -cne $dataset.sourceRevision) { throw 'Dataset provenance missing.' }
    $casePartitions = @(New-ServerTestPartitions -TestNames @('Suite.Test', 'Suite.test') -PartitionCount 2 -Durations $loaded.Durations)
    if ($casePartitions[1].FallbackTests -ne 1 -or $casePartitions[0].PredictedSeconds -ne 5) { throw 'Duration identity must be ordinal.' }
    # GIVEN malformed versions/provenance/weights WHEN loaded THEN they cannot silently fall back.
    foreach ($field in @('schemaVersion', 'sourceRevision', 'sourceRunUrl', 'fallbackSeconds', 'durations')) {
        $badDataset = $dataset.Clone(); $badDataset[$field] = 'invalid'
        $badDataset | ConvertTo-Json | Set-Content $datasetPath
        Assert-Rejected { Read-ServerTestDurations $datasetPath } 'dataset'
    }
}
finally { Remove-Item -LiteralPath $datasetRoot -Recurse -Force }
# GIVEN individually finite weights whose partition total overflows
# WHEN scheduled THEN invalid predicted totals are rejected.
Assert-Rejected { New-ServerTestPartitions -TestNames @('A', 'B', 'C', 'D') -PartitionCount 2 -Durations @{ A = 1.7e308; B = 1.7e308; C = 1.7e308; D = 1.7e308 } } 'duration'
# GIVEN CI evidence retention WHEN a partition run records its timing dataset
# THEN the uploaded artifact retains that dataset identity alongside results.
$workflow = Get-Content (Join-Path $PSScriptRoot '../../.github/workflows/ci.yml') -Raw
if (-not $workflow.Contains('artifacts/verification/**/duration-dataset.json')) { throw 'CI must retain duration dataset provenance.' }
