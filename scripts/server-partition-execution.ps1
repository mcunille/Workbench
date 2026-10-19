. (Join-Path $PSScriptRoot 'server-partition-contract.ps1')

function Invoke-ServerPartitionExecution {
    param([string]$RepositoryRoot, [string]$AssemblyPath, [object[]]$Partitions,
        [string]$ResultsRoot, [ValidateRange(1,4)][int]$MaxConcurrency=2)
    if (-not $Partitions.Count -or @($Partitions.Id | Sort-Object -Unique).Count -ne $Partitions.Count) { throw 'Missing or duplicate partition assignment.' }
    $jobs = [Collections.Generic.List[object]]::new()
    $results = [Collections.Generic.List[object]]::new()
    $failures = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Queue[object]]::new()
    foreach ($partition in $Partitions) { $pending.Enqueue($partition) }
    New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
    try {
        while ($pending.Count -gt 0 -or @($jobs | Where-Object { -not $_.Collected }).Count -gt 0) {
            while ($pending.Count -gt 0 -and @($jobs | Where-Object { -not $_.Collected }).Count -lt $MaxConcurrency) {
                $partition = $pending.Dequeue()
                $output = Join-Path $ResultsRoot "partition-$($partition.Id)"
                New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
                $filter = ($partition.Methods | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
                $settings = Join-Path $output 'tests.runsettings'
                Set-Content -LiteralPath $settings ('<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount><TestCaseFilter>' + [Security.SecurityElement]::Escape($filter) + '</TestCaseFilter></RunConfiguration><xUnit><ParallelizeAssembly>false</ParallelizeAssembly><ParallelizeTestCollections>false</ParallelizeTestCollections><MaxParallelThreads>1</MaxParallelThreads></xUnit></RunSettings>')
                $job = Start-Job -ScriptBlock {
                    param($Root, $Assembly, $Settings, $Output)
                    $ErrorActionPreference = 'Stop'
                    Set-Location -LiteralPath $Root
                    $env:DOTNET_CLI_UI_LANGUAGE = 'en'; $env:VSLANG = '1033'
                    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                    $OutputEncoding = [Text.UTF8Encoding]::new($false)
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    & dotnet vstest $Assembly "--Settings:$Settings" '--logger:trx;LogFileName=results.trx' "--ResultsDirectory:$Output" *> (Join-Path $Output 'test.log')
                    [pscustomobject]@{ ExitCode=$LASTEXITCODE; Seconds=$clock.Elapsed.TotalSeconds }
                } -ArgumentList $RepositoryRoot, $AssemblyPath, $settings, $output
                $jobs.Add([pscustomobject]@{ Job=$job; Partition=$partition; Root=$output; Collected=$false })
            }
            $active = @($jobs | Where-Object { -not $_.Collected })
            if ($active.Count) { Wait-Job -Job $active.Job -Any -Timeout 1 | Out-Null }
            foreach ($entry in $active) {
                if ($entry.Job.State -in @('Running','NotStarted')) { continue }
                $entry.Collected = $true
                try {
                    if ($entry.Job.State -ne 'Completed') { throw "Server partition $($entry.Partition.Id) job $($entry.Job.State)." }
                    $result = @(Receive-Job -Job $entry.Job -ErrorAction Stop)
                    if ($result.Count -ne 1 -or $null -eq $result[0].ExitCode) { throw "Server partition $($entry.Partition.Id) completion is missing." }
                    $record = [pscustomobject]@{ PartitionId=$entry.Partition.Id; ExitCode=$result[0].ExitCode; Seconds=$result[0].Seconds; TrxPath=(Join-Path $entry.Root 'results.trx'); LogPath=(Join-Path $entry.Root 'test.log') }
                    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $entry.Root 'timing.json')
                    Assert-ServerPartitionResult -Partition $entry.Partition -ResultPath $record.TrxPath -ExitCode $record.ExitCode
                    $results.Add($record)
                }
                catch { $failures.Add($_.Exception.Message); Write-Warning $_.Exception.Message }
            }
        }
        if ($jobs.Count -ne $Partitions.Count -or $results.Count -ne $Partitions.Count -or $failures.Count) { throw "Server partitions failed: $($failures -join ' ') Evidence: $ResultsRoot" }
        return $results.ToArray()
    }
    finally { foreach ($entry in $jobs) { Stop-Job -Job $entry.Job -ErrorAction SilentlyContinue; Remove-Job -Job $entry.Job -Force -ErrorAction SilentlyContinue } }
}
