[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dev-env.ps1')
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("workbench-migrate-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $connectionFile = Join-Path $temporaryRoot 'connection.txt'
    $connectionString = "Server=$env:WORKBENCH_SQL_HOST;Database=$env:WORKBENCH_DATABASE;User Id=$env:WORKBENCH_MIGRATOR_SQL_USER;Password=$env:WORKBENCH_MIGRATOR_SQL_PASSWORD;Encrypt=True;TrustServerCertificate=True"
    [System.IO.File]::WriteAllText($connectionFile, $connectionString)
    dotnet run --project (Join-Path $repositoryRoot 'src/Workbench.Database') -- `
        migrate --connection-file $connectionFile --expected-database $env:WORKBENCH_DATABASE
    if ($LASTEXITCODE -ne 0) { throw 'Workbench database migration failed.' }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
