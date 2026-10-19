$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/server-partition-contract.ps1')
$executor = Join-Path $PSScriptRoot '../../scripts/server-partition-execution.ps1'
if (Test-Path $executor) { . $executor }
function Assert-Rejected([scriptblock]$Action, [string]$Category) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -match $Category) { return }; throw }
    throw "Expected rejection: $Category"
}
# GIVEN theory cases differing only in case and Unicode, plus independent methods
$names = @('Suite.T(value: "A")', 'Suite.T(value: "a")', 'Suite.Unicode(value: "漢字")', 'Suite.C', 'Suite.D', 'Suite.E')
$parts = @(New-ServerTestPartitions -TestNames $names -PartitionCount 4)
if (-not (Get-Command New-DistributedServerPlan -ErrorAction SilentlyContinue)) { throw 'Global plan behavior is missing.' }
# WHEN planned once THEN four partitions cover every row and exactly two belong to each host.
$dataset = [pscustomobject]@{ Sha256 = ('a' * 64) }
$plan = New-DistributedServerPlan $names $parts $dataset @('tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj')
Assert-ServerTestCoverage $names @($plan.Partitions | ForEach-Object Tests)
if ($plan.Version -ne 1 -or $plan.Partitions.Count -ne 4 -or $plan.Hosts.Count -ne 2) { throw 'Invalid global shape.' }
foreach ($entry in $plan.Hosts) { if ($entry.PartitionIds.Count -ne 2) { throw 'Invalid host assignment.' } }
$reverse = @($names); [array]::Reverse($reverse)
$again = New-DistributedServerPlan $reverse @(New-ServerTestPartitions $reverse 4) $dataset @('tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj')
if (($plan | ConvertTo-Json -Depth 10 -Compress) -cne ($again | ConvertTo-Json -Depth 10 -Compress)) { throw 'Plan depends on discovery order.' }
# GIVEN skewed partition weights WHEN assigned THEN the best two-and-two pairing is used.
$skew = @(New-ServerTestPartitions @('Suite.A', 'Suite.B', 'Suite.C', 'Suite.D') 4 @{ 'Suite.A'=10; 'Suite.B'=9; 'Suite.C'=2; 'Suite.D'=1 })
$balanced = New-DistributedServerPlan @('Suite.A', 'Suite.B', 'Suite.C', 'Suite.D') $skew $dataset @('tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj')
$hostLoads = @($balanced.Hosts | ForEach-Object { $hostEntry = $_; ($balanced.Partitions | Where-Object { $_.Id -in $hostEntry.PartitionIds } | Measure-Object PredictedSeconds -Sum).Sum })
if (($hostLoads -join ',') -ne '11,11') { throw 'Host pairing is not balanced.' }
Assert-Rejected { New-DistributedServerPlan $names $parts[0..2] $dataset @('tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj') } 'four'
Assert-Rejected { New-DistributedServerPlan $names $parts $dataset @('new.csproj') } 'project inventory'
Assert-Rejected { New-DistributedServerPlan $names @($parts[0],$parts[0],$parts[2],$parts[3]) $dataset @('tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj') } 'coverage|duplicate'

$root = Join-Path ([IO.Path]::GetTempPath()) "distributed-execution-$([guid]::NewGuid().ToString('N'))"
$oldPath = $env:PATH
$oldNames = $env:WORKBENCH_TEST_NAMES
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $names | ConvertTo-Json | Set-Content (Join-Path $root 'names.json')
    $env:WORKBENCH_TEST_NAMES = Join-Path $root 'names.json'
    # GIVEN a real separate native-boundary process emitting TRX and start/end evidence
    @'
$ErrorActionPreference = 'Stop'
$output = ($args | Where-Object { $_ -like '--ResultsDirectory:*' }).Substring('--ResultsDirectory:'.Length)
$settings = ($args | Where-Object { $_ -like '--Settings:*' }).Substring('--Settings:'.Length)
[xml]$xml = Get-Content $settings -Raw
if ($xml.RunSettings.RunConfiguration.MaxCpuCount -ne 1 -or $xml.RunSettings.xUnit.MaxParallelThreads -ne 1 -or $xml.RunSettings.xUnit.ParallelizeTestCollections -cne 'false') { exit 23 }
$methods = $xml.RunSettings.RunConfiguration.TestCaseFilter.Split('|') | ForEach-Object { $_ -replace '^FullyQualifiedName=', '' }
$selected = @(Get-Content $env:WORKBENCH_TEST_NAMES -Raw | ConvertFrom-Json | Where-Object { ($_ -replace '\(.*$', '') -cin $methods })
$started = [DateTime]::UtcNow.Ticks
Start-Sleep -Milliseconds 750
$rows = ($selected | ForEach-Object { '<UnitTestResult testName="' + [Security.SecurityElement]::Escape($_) + '" outcome="Passed" />' }) -join ''
Set-Content (Join-Path $output 'results.trx') "<TestRun><Results>$rows</Results></TestRun>"
@{ Start=$started; End=[DateTime]::UtcNow.Ticks; Pid=$PID } | ConvertTo-Json | Set-Content (Join-Path $output 'process.json')
if ((Test-Path (Join-Path (Split-Path $env:WORKBENCH_TEST_NAMES) 'fail')) -and (Split-Path $output -Leaf) -eq 'partition-1') { exit 7 }
exit 0
'@ | Set-Content (Join-Path $root 'dotnet.ps1')
    $env:PATH = "$root$([IO.Path]::PathSeparator)$oldPath"
    # WHEN four partitions execute with two slots THEN only two children overlap and all finish.
    $results = @(Invoke-ServerPartitionExecution -RepositoryRoot $root -AssemblyPath (Join-Path $root 'test.dll') -Partitions $plan.Partitions -ResultsRoot (Join-Path $root 'success') -MaxConcurrency 2)
    if ($results.Count -ne 4) { throw 'Missing partition results.' }
    $intervals = @(Get-ChildItem (Join-Path $root 'success') -Filter process.json -Recurse | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
    $peak = 0
    foreach ($point in $intervals.Start) { $peak = [Math]::Max($peak, @($intervals | Where-Object { $_.Start -le $point -and $_.End -gt $point }).Count) }
    if ($peak -ne 2 -or $intervals.Count -ne 4) { throw "Expected two concurrent joined children; found $peak/$($intervals.Count)." }
    # GIVEN a successful-looking TRX but failed process WHEN joined THEN fail without losing sibling evidence.
    Set-Content (Join-Path $root 'fail') ''
    Assert-Rejected { Invoke-ServerPartitionExecution $root (Join-Path $root 'test.dll') $plan.Partitions (Join-Path $root 'failure') 2 } 'exit code 7'
    if (@(Get-ChildItem (Join-Path $root 'failure') -Filter timing.json -Recurse).Count -ne 4) { throw 'Failed execution did not join all partitions.' }
    Write-Host 'Distributed plan and real process execution passed.'
}
finally {
    $env:PATH = $oldPath; $env:WORKBENCH_TEST_NAMES = $oldNames
    $resolved = [IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('distributed-execution-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
