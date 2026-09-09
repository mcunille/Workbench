[CmdletBinding()]
param(
    [ValidateRange(2, 4)][int]$PartitionCount = 2,
    [ValidateRange(1, 4)][int]$MaxConcurrency = 2,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'server-partition-contract.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj'
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $repositoryRoot 'artifacts/test-results' }
# A unique invocation directory prevents stale TRX evidence from satisfying this run.
$runRoot = Join-Path ([IO.Path]::GetFullPath($ResultsDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$jobs = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()
$priorLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$priorVsLanguage = $env:VSLANG
Push-Location $repositoryRoot
try {
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    $env:VSLANG = '1033'
    if (-not $NoBuild) {
        dotnet restore $project --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "Server test restore failed with exit code $LASTEXITCODE." }
        dotnet build $project --configuration $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Server test build failed with exit code $LASTEXITCODE." }
    }
    # Evaluate MSBuild's test-project property (including imports), so a new solution
    # test assembly cannot silently disappear from the former solution-wide gate.
    [xml]$solution = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Workbench.slnx') -Raw
    $testProjects = @(
        foreach ($node in $solution.SelectNodes('//Project')) {
            $solutionProject = Join-Path $repositoryRoot $node.Path
            $isTestProject = @(dotnet msbuild $solutionProject "-p:Configuration=$Configuration" '-getProperty:IsTestProject')
            if ($LASTEXITCODE -ne 0) { throw "Solution test project discovery failed for '$($node.Path)'." }
            if (($isTestProject -join '').Trim() -ieq 'true') { $solutionProject }
        }
    )
    Assert-ServerTestProjects -TestProjects $testProjects -SupportedProject $project
    $names = @(Invoke-ServerTestDiscovery -LogPath (Join-Path $runRoot 'discovery.log') -Command {
        dotnet test $project --configuration $Configuration --no-build --no-restore --list-tests
    })
    $partitions = @(New-ServerTestPartitions -TestNames $names -PartitionCount $PartitionCount)
    $partitions | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'inventory.json')
    Write-Host "Discovered $($names.Count) tests; partition sizes: $(($partitions | ForEach-Object { $_.Tests.Count }) -join ', '); max concurrent SQL containers: $([Math]::Min($PartitionCount, $MaxConcurrency))."
    $pending = [Collections.Generic.Queue[object]]::new()
    foreach ($partition in $partitions) { $pending.Enqueue($partition) }
    while ($pending.Count -gt 0 -or @($jobs | Where-Object { -not $_.Collected }).Count -gt 0) {
        while ($pending.Count -gt 0 -and @($jobs | Where-Object { -not $_.Collected }).Count -lt $MaxConcurrency) {
            $partition = $pending.Dequeue()
            $partitionRoot = Join-Path $runRoot "partition-$($partition.Id)"
            New-Item -ItemType Directory -Path $partitionRoot | Out-Null
            $filter = ($partition.Methods | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
            $settingsPath = Join-Path $partitionRoot 'tests.runsettings'
            $settings = '<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount><TestCaseFilter>' + [Security.SecurityElement]::Escape($filter) + '</TestCaseFilter></RunConfiguration><xUnit><ParallelizeAssembly>false</ParallelizeAssembly><ParallelizeTestCollections>false</ParallelizeTestCollections><MaxParallelThreads>1</MaxParallelThreads></xUnit></RunSettings>'
            Set-Content -LiteralPath $settingsPath $settings
            $job = Start-Job -ScriptBlock {
                param($Root, $Project, $BuildConfiguration, $Settings, $OutputDirectory)
                Set-Location $Root
                $timer = [Diagnostics.Stopwatch]::StartNew()
                & dotnet test $Project --configuration $BuildConfiguration --no-build --no-restore --settings $Settings --logger 'trx;LogFileName=results.trx' --results-directory $OutputDirectory *> (Join-Path $OutputDirectory 'test.log')
                [pscustomobject]@{ ExitCode = $LASTEXITCODE; Seconds = $timer.Elapsed.TotalSeconds }
            } -ArgumentList $repositoryRoot, $project, $Configuration, $settingsPath, $partitionRoot
            $jobs.Add([pscustomobject]@{ Job = $job; Partition = $partition; Root = $partitionRoot; Collected = $false })
        }
        $activeJobs = @($jobs | Where-Object { -not $_.Collected })
        if ($activeJobs.Count -gt 0) { Wait-Job -Job $activeJobs.Job -Any -Timeout 1 | Out-Null }
        foreach ($entry in $activeJobs) {
            if ($entry.Job.State -in @('Running', 'NotStarted')) { continue }
            $entry.Collected = $true
            try {
                if ($entry.Job.State -ne 'Completed') { throw "Server partition $($entry.Partition.Id) job $($entry.Job.State)." }
                $result = @(Receive-Job $entry.Job -ErrorAction Stop)
                if ($result.Count -ne 1 -or $null -eq $result[0].ExitCode) { throw "Server partition $($entry.Partition.Id) completion is missing." }
                $result[0] | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $entry.Root 'timing.json')
                Assert-ServerPartitionResult -Partition $entry.Partition -ResultPath (Join-Path $entry.Root 'results.trx') -ExitCode $result[0].ExitCode
                Write-Host "Server partition $($entry.Partition.Id): $($entry.Partition.Tests.Count) passed in $([Math]::Round($result[0].Seconds, 1)) seconds."
            }
            catch { $failures.Add($_.Exception.Message); Write-Warning $_.Exception.Message }
        }
    }
    if ($jobs.Count -ne $PartitionCount -or @($jobs | Where-Object { -not $_.Collected }).Count -ne 0) { throw 'Required server partition completion is missing.' }
    if ($failures.Count -gt 0) { throw "Server partitions failed: $($failures -join ' ') Evidence: $runRoot" }
    Write-Host "All $PartitionCount server partitions passed; exact inventory coverage verified. Evidence: $runRoot"
}
finally {
    foreach ($entry in $jobs) { Stop-Job $entry.Job -ErrorAction SilentlyContinue; Remove-Job $entry.Job -Force -ErrorAction SilentlyContinue }
    $env:DOTNET_CLI_UI_LANGUAGE = $priorLanguage
    $env:VSLANG = $priorVsLanguage
    Pop-Location
}
