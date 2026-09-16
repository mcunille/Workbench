[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$SourceRunUrl,
    [Parameter(Mandatory)][string]$SourceRevision,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'server-test-durations.json')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Artifacts are untrusted data: never load scripts or follow paths stored inside them.
if ($SourceRunUrl -cnotmatch '^https://github\.com/mcunille/Workbench/actions/runs/[0-9]+$') {
    throw 'SourceRunUrl must identify a Workbench GitHub Actions run.'
}
if ($SourceRevision -notmatch '^[0-9a-fA-F]{40}$') { throw 'SourceRevision must be a full 40-character Git revision.' }
$gate = Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'gate.json') -Raw | ConvertFrom-Json -AsHashtable
if ($gate['Succeeded'] -isnot [bool] -or -not $gate['Succeeded']) { throw 'Evidence must describe a completed successful gate.' }
$inventories = @(Get-ChildItem -LiteralPath (Join-Path $EvidenceDirectory 'server-tests') -Directory | ForEach-Object {
    $candidate = Join-Path $_.FullName 'inventory.json'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidate }
})
if ($inventories.Count -ne 1) { throw 'Evidence must contain exactly one server inventory.' }
$inventory = @(Get-Content -LiteralPath $inventories[0] -Raw | ConvertFrom-Json -AsHashtable)
if ($inventory.Count -eq 0 -or $gate['ServerPartitions'] -ne $inventory.Count) { throw 'Gate partition count does not match inventory.' }
$serverDirectory = Split-Path $inventories[0]
$partitionDirectories = @(Get-ChildItem -LiteralPath $serverDirectory -Directory -Filter 'partition-*')
if ($partitionDirectories.Count -ne $inventory.Count) { throw 'Evidence partition directories do not match inventory.' }
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$partitionIds = [Collections.Generic.HashSet[int]]::new()
$durations = [Collections.Generic.SortedDictionary[string,double]]::new([StringComparer]::Ordinal)
foreach ($partition in $inventory) {
    $id = 0
    if (-not [int]::TryParse([string]$partition['Id'], [ref]$id) -or $id -lt 1 -or -not $partitionIds.Add($id)) {
        throw 'Inventory contains an invalid or duplicate partition ID.'
    }
    $partitionExpected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in @($partition['Tests'])) {
        if ($name -isnot [string] -or $name.Length -eq 0) { throw 'Inventory contains an empty or invalid test name.' }
        if (-not $expected.Add($name)) { throw "Inventory contains duplicate test '$name'." }
        [void]$partitionExpected.Add($name)
    }
    if ($partitionExpected.Count -eq 0) { throw 'Inventory contains an empty partition.' }
    $partitionDirectory = Join-Path $serverDirectory "partition-$id"
    $timing = Get-Content -LiteralPath (Join-Path $partitionDirectory 'timing.json') -Raw | ConvertFrom-Json -AsHashtable
    if (-not $timing.ContainsKey('ExitCode') -or $timing['ExitCode'] -isnot [long] -and $timing['ExitCode'] -isnot [int] -or $timing['ExitCode'] -ne 0) {
        throw "Partition $id must have exit code 0."
    }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create((Join-Path $partitionDirectory 'results.trx'), $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally { $reader.Dispose() }
    $namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $rows = @($document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $namespaces))
    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows) {
        $name = $row.GetAttribute('testName')
        if (-not $actual.Add($name)) { throw "Result contains duplicate test '$name'." }
        if ($row.GetAttribute('outcome') -cne 'Passed') { throw "Result outcome for '$name' is not Passed." }
        $duration = [TimeSpan]::Zero
        if (-not [TimeSpan]::TryParseExact($row.GetAttribute('duration'), 'c', [Globalization.CultureInfo]::InvariantCulture, [ref]$duration) -or $duration.TotalSeconds -lt 0) {
            throw "Result duration for '$name' is invalid."
        }
        $seconds = $duration.TotalSeconds
        if ($seconds -eq 0) { $seconds = 0.001 }
        if ($durations.ContainsKey($name)) { throw "Results contain duplicate test '$name'." }
        $durations.Add($name, $seconds)
    }
    if (-not $partitionExpected.SetEquals($actual)) { throw "Partition $id result coverage differs from inventory." }
}
if (-not $expected.SetEquals($durations.Keys)) { throw 'Result coverage differs from full inventory.' }
$baseline = [ordered]@{
    schemaVersion = 1
    sourceRunUrl = $SourceRunUrl
    sourceRevision = $SourceRevision.ToLowerInvariant()
    fallbackSeconds = 1.0
    durations = $durations
}
$baseline | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
