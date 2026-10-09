param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$ExpectedCases)
$ErrorActionPreference = 'Stop'
Set-Location $Repository
. ./scripts/server-partition-contract.ps1
$root = Join-Path $PWD 'artifacts/hosted-coverage'
New-Item -ItemType Directory $root | Out-Null
dotnet restore Workbench.slnx --locked-mode
if ($LASTEXITCODE) { throw 'Locked restore failed.' }
dotnet build Workbench.slnx --configuration Release --no-restore -p:UseAppHost=false -p:BuildClient=false
if ($LASTEXITCODE) { throw 'Current-source build failed.' }
$project = 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj'
$names = @(Invoke-ServerTestDiscovery -LogPath "$root/discovery.log" -Command {
    dotnet test $project --configuration Release --no-build --no-restore --list-tests
})
if ($names.Count -ne $ExpectedCases) { throw "Expected $ExpectedCases cases; discovered $($names.Count)." }
# Validate every discovered identity, then assert the complete single-process inventory.
$null = New-ServerTestPartitions -TestNames $names -PartitionCount 2
$inventory = [pscustomobject]@{Id=1;Tests=$names}
$inventory | ConvertTo-Json -Depth 4 | Set-Content "$root/inventory.json"
dotnet test $project --configuration Release --no-build --no-restore --settings tests/coverage.runsettings `
    --collect 'XPlat Code Coverage' --logger 'trx;LogFileName=server.trx' --results-directory $root
$serverExit = $LASTEXITCODE
Assert-ServerPartitionResult -Partition $inventory -ResultPath "$root/server.trx" -ExitCode $serverExit
$reports = @(Get-ChildItem $root -Recurse -Filter coverage.cobertura.xml |
    Where-Object { $_.Directory.Name -match '^[0-9a-f]{8}-[0-9a-f-]{27}$' })
if ($reports.Count -ne 1) { throw 'Expected exactly one current server coverage report.' }
[xml]$coverage = Get-Content $reports[0].FullName -Raw
foreach ($counter in @('lines-covered','lines-valid','branches-covered','branches-valid')) {
    if (-not $coverage.coverage.HasAttribute($counter)) { throw "Coverage counter $counter is missing." }
}
$modules = @($coverage.coverage.packages.package | ForEach-Object name | Sort-Object -Unique)
if ('Workbench.Server' -notin $modules -or @($modules | Where-Object { $_ -notin @('Workbench.Server','Workbench.Database') }).Count) {
    throw 'Coverage production module scope changed.'
}
$summary = [pscustomobject]@{
    Commit=(git rev-parse HEAD).Trim();Cases=$names.Count;ServerSucceeded=$true
    Modules=$modules
    LinesCovered=[long]$coverage.coverage.'lines-covered';LinesTotal=[long]$coverage.coverage.'lines-valid'
    BranchesCovered=[long]$coverage.coverage.'branches-covered';BranchesTotal=[long]$coverage.coverage.'branches-valid'
}
if ($summary.LinesTotal -le 0 -or $summary.BranchesTotal -le 0 -or
    $summary.LinesCovered -lt 0 -or $summary.LinesCovered -gt $summary.LinesTotal -or
    $summary.BranchesCovered -lt 0 -or $summary.BranchesCovered -gt $summary.BranchesTotal) {
    throw 'Coverage totals or covered counts are invalid.'
}
$summary | ConvertTo-Json | Set-Content "$root/server-summary.json"
npm ci --prefix src/Workbench.Client --ignore-scripts --no-audit --no-fund
if ($LASTEXITCODE) { throw 'Client locked install failed.' }
npm run test:run --prefix src/Workbench.Client -- --coverage --maxWorkers=1 --coverage.reportsDirectory=../../artifacts/hosted-coverage/client
if ($LASTEXITCODE) { throw 'Client coverage failed.' }

