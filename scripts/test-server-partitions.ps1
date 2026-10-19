[CmdletBinding()]
param(
    [ValidateRange(2, 4)][int]$PartitionCount = 2,
    [ValidateRange(1, 4)][int]$MaxConcurrency = 2,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$ResultsDirectory,
    [string]$TimingDataPath = (Join-Path $PSScriptRoot 'server-test-durations.json')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'server-partition-execution.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj'
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $repositoryRoot 'artifacts/test-results' }
# A unique invocation directory prevents stale TRX evidence from satisfying this run.
$runRoot = Join-Path ([IO.Path]::GetFullPath($ResultsDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
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
    $timings = Read-ServerTestDurations -Path $TimingDataPath
    $partitions = @(New-ServerTestPartitions -TestNames $names -PartitionCount $PartitionCount -Durations $timings.Durations -FallbackSeconds $timings.FallbackSeconds)
    $timings | Select-Object SchemaVersion, SourceRevision, SourceRunUrl, Sha256, FallbackSeconds |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'duration-dataset.json')
    Write-Host "Duration dataset $($timings.Sha256) from $($timings.SourceRunUrl); predicted seconds: $(($partitions.PredictedSeconds | ForEach-Object { [Math]::Round($_, 3) }) -join ', '); fallback tests: $(($partitions.FallbackTests | Measure-Object -Sum).Sum)."
    $partitions | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'inventory.json')
    Write-Host "Discovered $($names.Count) tests; partition sizes: $(($partitions | ForEach-Object { $_.Tests.Count }) -join ', '); max concurrent SQL containers: $([Math]::Min($PartitionCount, $MaxConcurrency))."
    $assembly = Join-Path $repositoryRoot "tests/Workbench.Server.IntegrationTests/bin/$Configuration/net10.0/Workbench.Server.IntegrationTests.dll"
    $null = Invoke-ServerPartitionExecution -RepositoryRoot $repositoryRoot -AssemblyPath $assembly -Partitions $partitions -ResultsRoot $runRoot -MaxConcurrency $MaxConcurrency
    Write-Host "All $PartitionCount server partitions passed; exact inventory coverage verified. Evidence: $runRoot"
}
finally {
    $env:DOTNET_CLI_UI_LANGUAGE = $priorLanguage
    $env:VSLANG = $priorVsLanguage
    Pop-Location
}
