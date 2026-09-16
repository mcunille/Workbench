function Invoke-ServerTestDiscovery {
    param([scriptblock]$Command, [string]$LogPath)
    # dotnet emits UTF-8 even when a Windows background job starts with OEM console
    # decoding. Configure the native boundary explicitly, then restore its caller.
    $priorConsoleEncoding = [Console]::OutputEncoding
    $priorOutputEncoding = $OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $OutputEncoding = [Text.UTF8Encoding]::new($false)
        $discovery = @(& $Command 2>&1)
    }
    finally {
        [Console]::OutputEncoding = $priorConsoleEncoding
        $OutputEncoding = $priorOutputEncoding
    }
    if ($LASTEXITCODE -ne 0) { throw "Server test discovery failed with exit code $LASTEXITCODE`: $($discovery -join [Environment]::NewLine)" }
    if ($LogPath) { $discovery | Set-Content -LiteralPath $LogPath }
    $header = -1
    for ($index = 0; $index -lt $discovery.Count; $index++) {
        if ([string]$discovery[$index] -match '^The following Tests are available:') { $header = $index; break }
    }
    if ($header -lt 0) { throw 'Server test discovery inventory header is missing.' }
    $names = [Collections.Generic.List[string]]::new()
    foreach ($entry in ($discovery | Select-Object -Skip ($header + 1))) {
        $line = [string]$entry
        if ($line.Length -eq 0) { continue }
        if (-not $line.StartsWith('    ', [StringComparison]::Ordinal)) {
            throw "Unexpected server discovery output after inventory header: $line"
        }
        # VSTest adds exactly four spaces. Custom display-name whitespace is identity,
        # not formatting: preserve it so unsupported names fail rather than disappear.
        $names.Add($line.Substring(4))
    }
    return $names.ToArray()
}

function Assert-ServerTestProjects {
    param([string[]]$TestProjects, [string]$SupportedProject)
    if ($TestProjects.Count -ne 1 -or $TestProjects[0] -cne $SupportedProject) {
        throw "Solution test project inventory changed; explicitly include every test project in the server partition contract. Expected '$SupportedProject'; found '$($TestProjects -join ', ')'."
    }
}

function New-ServerTestPartitions {
    param(
        [AllowEmptyCollection()][string[]]$TestNames,
        [ValidateRange(2, 4)][int]$PartitionCount,
        [System.Collections.IDictionary]$Durations = @{},
        [double]$FallbackSeconds = 1
    )
    if (-not [double]::IsFinite($FallbackSeconds) -or $FallbackSeconds -le 0) { throw 'Invalid fallback duration.' }
    foreach ($value in $Durations.Values) {
        if ($value -is [string] -or $value -is [bool] -or $null -eq $value -or
            -not [double]::IsFinite([double]$value) -or [double]$value -le 0) { throw 'Invalid historical duration.' }
    }
    $weights = [Collections.Generic.Dictionary[string, double]]::new([StringComparer]::Ordinal)
    foreach ($key in $Durations.Keys) { $weights.Add($key, [double]$Durations[$key]) }
    if ($TestNames.Count -eq 0) { throw 'Discovered server test inventory is empty.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $methods = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($name in $TestNames) {
        if (-not $seen.Add($name)) { throw "Discovered server test inventory contains a duplicate: $name" }
        # xUnit display names contain the fully qualified method followed by optional theory arguments.
        if ($name -notmatch '^(?<method>[A-Za-z_][A-Za-z0-9_.+`]*)(?:\(.*\))?$') {
            throw "Unsupported discovered server test name: $name"
        }
        $method = $Matches.method
        if (-not $methods.ContainsKey($method)) { $methods[$method] = [Collections.Generic.List[string]]::new() }
        $methods[$method].Add($name)
    }
    if ($methods.Count -lt $PartitionCount) { throw 'Not enough test methods for nonempty required partitions.' }
    $partitions = @(1..$PartitionCount | ForEach-Object {
        [pscustomobject]@{ Id = $_; Methods = [Collections.Generic.List[string]]::new(); Tests = [Collections.Generic.List[string]]::new(); PredictedSeconds = 0.0; FallbackTests = 0 }
    })
    # Ordinal ties make assignment independent of culture and discovery order.
    $methodNames = [string[]]@($methods.Keys)
    [array]::Sort($methodNames, [StringComparer]::Ordinal)
    $groups = @(foreach ($method in $methodNames) {
        $rows = $methods[$method].ToArray()
        [array]::Sort($rows, [StringComparer]::Ordinal)
        $seconds = 0.0
        $fallback = 0
        foreach ($row in $rows) {
            if ($weights.ContainsKey($row)) { $seconds += $weights[$row] }
            else { $seconds += $FallbackSeconds; $fallback++ }
        }
        if (-not [double]::IsFinite($seconds)) { throw 'Aggregated duration is not finite.' }
        [pscustomobject]@{ Method = $method; Rows = $rows; Seconds = $seconds; Fallback = $fallback }
    })
    foreach ($group in ($groups | Sort-Object -Stable -Property @{ Expression = { $_.Seconds }; Descending = $true })) {
        $partition = $partitions | Sort-Object PredictedSeconds, Id | Select-Object -First 1
        $partition.Methods.Add($group.Method)
        $partition.Tests.AddRange([string[]]$group.Rows)
        $partition.PredictedSeconds += $group.Seconds
        if (-not [double]::IsFinite($partition.PredictedSeconds)) { throw 'Partition duration is not finite.' }
        $partition.FallbackTests += $group.Fallback
    }
    Assert-ServerTestPartitions -TestNames $TestNames -Partitions $partitions
    return $partitions
}

function Assert-ServerTestPartitions {
    param([string[]]$TestNames, [object[]]$Partitions)
    $assigned = [Collections.Generic.List[string]]::new()
    foreach ($partition in $Partitions) {
        if ($partition.Tests.Count -eq 0) { throw 'A required server partition is unexpectedly empty.' }
        foreach ($name in $partition.Tests) { $assigned.Add($name) }
    }
    Assert-ServerTestCoverage -Expected $TestNames -Actual $assigned.ToArray()
}

function Assert-ServerTestCoverage {
    param([string[]]$Expected, [AllowEmptyCollection()][string[]]$Actual)
    $expectedNames = @($Expected | Sort-Object -CaseSensitive)
    $actualNames = @($Actual | Sort-Object -CaseSensitive)
    if ($expectedNames.Count -ne $actualNames.Count) {
        throw "Server test coverage mismatch: expected $($expectedNames.Count) rows; found $($actualNames.Count)."
    }
    for ($index = 0; $index -lt $expectedNames.Count; $index++) {
        if ($expectedNames[$index] -cne $actualNames[$index]) {
            throw "Server test coverage mismatch: expected '$($expectedNames[$index])'; found '$($actualNames[$index])'."
        }
    }
}

function Assert-ServerPartitionResult {
    param([object]$Partition, [string]$ResultPath, [int]$ExitCode)
    if ($ExitCode -ne 0) { throw "Server partition $($Partition.Id) failed with exit code $ExitCode." }
    if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) { throw "Server partition $($Partition.Id) result evidence is missing." }
    [xml]$document = Get-Content -LiteralPath $ResultPath -Raw
    $results = @($document.SelectNodes('//*[local-name()="UnitTestResult"]'))
    Assert-ServerTestCoverage -Expected $Partition.Tests -Actual @($results | ForEach-Object { $_.testName })
    foreach ($result in $results) {
        if ($result.outcome -cne 'Passed') { throw "Server partition $($Partition.Id) has non-passing outcome '$($result.outcome)' for '$($result.testName)'." }
    }
}
function Read-ServerTestDurations {
    param([string]$Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $data = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable
    if ($data.schemaVersion -ne 1 -or $data.durations -isnot [System.Collections.IDictionary] -or
        $data.sourceRevision -cnotmatch '^[a-f0-9]{40}$' -or
        $data.sourceRunUrl -cnotmatch '^https://github\.com/mcunille/Workbench/actions/runs/[0-9]+$' -or
        $data.fallbackSeconds -is [string] -or $data.fallbackSeconds -is [bool] -or
        -not [double]::IsFinite([double]$data.fallbackSeconds) -or $data.fallbackSeconds -le 0) {
        throw 'Invalid server duration dataset.'
    }
    # Validate every weight, including historical tests no longer in discovery.
    $null = New-ServerTestPartitions -TestNames @('Validation.A', 'Validation.B') -PartitionCount 2 -Durations $data.durations -FallbackSeconds $data.fallbackSeconds
    return [pscustomobject]@{
        Durations = $data.durations; FallbackSeconds = $data.fallbackSeconds
        SchemaVersion = $data.schemaVersion; SourceRevision = $data.sourceRevision; SourceRunUrl = $data.sourceRunUrl
        Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
}
