[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dev-env.ps1')

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("workbench-bootstrap-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $connectionFile = Join-Path $temporaryRoot 'connection.txt'
    $passwordFile = Join-Path $temporaryRoot 'admin-password.txt'
    $webPasswordFile = Join-Path $temporaryRoot 'web-password.txt'
    $operatorPasswordFile = Join-Path $temporaryRoot 'operator-password.txt'
    $migratorPasswordFile = Join-Path $temporaryRoot 'migrator-password.txt'
    $tenantContextProofKeyFile = Join-Path $temporaryRoot 'tenant-context-proof-key.txt'
    $connectionString = "Server=$env:WORKBENCH_SQL_HOST;Database=$env:WORKBENCH_DATABASE;User Id=$env:WORKBENCH_SETUP_SQL_USER;Password=$env:WORKBENCH_SETUP_SQL_PASSWORD;Encrypt=True;TrustServerCertificate=True"
    [System.IO.File]::WriteAllText($connectionFile, $connectionString)
    [System.IO.File]::WriteAllText($passwordFile, $env:WORKBENCH_DEV_ADMIN_PASSWORD)
    [System.IO.File]::WriteAllText($webPasswordFile, $env:WORKBENCH_WEB_SQL_PASSWORD)
    [System.IO.File]::WriteAllText($operatorPasswordFile, $env:WORKBENCH_OPERATOR_SQL_PASSWORD)
    [System.IO.File]::WriteAllText($migratorPasswordFile, $env:WORKBENCH_MIGRATOR_SQL_PASSWORD)
    [System.IO.File]::WriteAllText($tenantContextProofKeyFile, $env:WORKBENCH_TENANT_CONTEXT_PROOF_KEY)

    dotnet run --project (Join-Path $repositoryRoot 'src/Workbench.Database') -- `
        migrate --connection-file $connectionFile --expected-database $env:WORKBENCH_DATABASE
    if ($LASTEXITCODE -ne 0) { throw 'Workbench database migration failed.' }

    dotnet run --project (Join-Path $repositoryRoot 'src/Workbench.Database') -- `
        principals provision --connection-file $connectionFile --expected-database $env:WORKBENCH_DATABASE `
        --web-user $env:WORKBENCH_WEB_SQL_USER --web-password-file $webPasswordFile `
        --operator-user $env:WORKBENCH_OPERATOR_SQL_USER --operator-password-file $operatorPasswordFile `
        --migrator-user $env:WORKBENCH_MIGRATOR_SQL_USER --migrator-password-file $migratorPasswordFile `
        --tenant-context-proof-key-file $tenantContextProofKeyFile
    if ($LASTEXITCODE -ne 0) { throw 'Workbench database-principal provisioning failed.' }

    $operatorConnection = "Server=$env:WORKBENCH_SQL_HOST;Database=$env:WORKBENCH_DATABASE;User Id=$env:WORKBENCH_OPERATOR_SQL_USER;Password=$env:WORKBENCH_OPERATOR_SQL_PASSWORD;Encrypt=True;TrustServerCertificate=True"
    [System.IO.File]::WriteAllText($connectionFile, $operatorConnection)

    dotnet run --project (Join-Path $repositoryRoot 'src/Workbench.Database') -- `
        bootstrap --connection-file $connectionFile --expected-database $env:WORKBENCH_DATABASE `
        --tenant-name $env:WORKBENCH_DEV_TENANT_NAME --admin-email $env:WORKBENCH_DEV_ADMIN_EMAIL `
        --password-file $passwordFile
    if ($LASTEXITCODE -ne 0) { throw 'Workbench bootstrap failed.' }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
