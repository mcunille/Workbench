[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Start', 'Stop', 'Status')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$stateRoot = Join-Path $repositoryRoot 'artifacts/sql'
$containerName = 'workbench-test-sql'
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker -and $IsWindows) {
    $candidate = 'C:\Program Files\Docker\Docker\resources\bin\docker.exe'
    if (Test-Path -LiteralPath $candidate) { $docker = Get-Item -LiteralPath $candidate }
}
if (-not $docker) { throw 'Docker CLI is required.' }

$existing = & $docker.Source ps --all --quiet --filter "name=^/${containerName}$"
if ($LASTEXITCODE -ne 0) { throw 'Docker container lookup failed.' }

if ($Action -eq 'Status') {
    Write-Host $(if ($existing) { 'Workbench test SQL exists.' } else { 'Workbench test SQL is stopped.' })
    return
}

if ($Action -eq 'Stop') {
    if ($existing) { & $docker.Source rm --force $containerName | Out-Null }
    if (Test-Path -LiteralPath $stateRoot) {
        $resolved = [IO.Path]::GetFullPath($stateRoot)
        if (-not $resolved.StartsWith([IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected SQL state path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    Write-Host 'Workbench test SQL stopped and its local credentials removed.'
    return
}

if ($existing) { throw 'Workbench test SQL already exists; stop it before starting a new instance.' }
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$password = "Wb-{0}-aA9!" -f ([Guid]::NewGuid().ToString('N'))
$environmentFile = Join-Path $stateRoot 'container.env'
$connectionFile = Join-Path $stateRoot 'connection.txt'
Set-Content -LiteralPath $environmentFile -Value @("ACCEPT_EULA=Y", "MSSQL_SA_PASSWORD=$password")
Set-Content -LiteralPath $connectionFile -Value "Server=127.0.0.1,14333;Database=master;User Id=sa;Password=$password;Encrypt=True;TrustServerCertificate=True"
try {
    & $docker.Source run --detach --name $containerName --env-file $environmentFile `
        --publish '127.0.0.1:14333:1433' `
        'mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'SQL Server container start failed.' }
    Write-Host 'Workbench test SQL started; ignored credentials are under artifacts/sql.'
}
catch {
    if (Test-Path -LiteralPath $stateRoot) { Remove-Item -LiteralPath $stateRoot -Recurse -Force }
    throw
}
