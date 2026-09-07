[CmdletBinding()]
param([string]$Docker = 'docker')

$ErrorActionPreference = 'Stop'
$restoreScript = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/restore-database.ps1'
$container = "workbench-restore-proof-$([Guid]::NewGuid().ToString('N'))"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) $container
$previousSqlPassword = $env:SQLCMDPASSWORD
$previousSaPassword = $env:MSSQL_SA_PASSWORD
$env:MSSQL_SA_PASSWORD = "Test-$([Guid]::NewGuid().ToString('N'))!"
$env:SQLCMDPASSWORD = $env:MSSQL_SA_PASSWORD
$started = $false

function Invoke-TestSql([string]$Query, [string]$Database = 'master') {
    # The private disposable container has no published ports. Certificate bypass is test-only.
    $output = & $Docker exec --env SQLCMDPASSWORD $container /opt/mssql-tools18/bin/sqlcmd `
        -S localhost -U sa -d $Database -C -b -h -1 -W -Q "SET NOCOUNT ON; $Query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("Disposable SQL command failed: $($output -join ' ')") }
    return ($output -join "`n").Trim()
}

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    & $Docker run --detach --name $container --env ACCEPT_EULA=Y --env MSSQL_SA_PASSWORD `
        mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Disposable SQL container failed to start.' }
    $started = $true
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try { $null = Invoke-TestSql 'SELECT 1'; $ready = $true; break }
        catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ready) { throw 'Disposable SQL server did not become ready.' }

    # GIVEN a multi-user backup containing historical authority and an ordinary contained user.
    $null = Invoke-TestSql @'
EXEC sp_configure 'contained database authentication', 1;
RECONFIGURE;
CREATE DATABASE RestoreProof;
ALTER DATABASE RestoreProof SET CONTAINMENT = PARTIAL;
'@
    $null = Invoke-TestSql @'
CREATE USER web_probe WITH PASSWORD = 'Disposable-Probe-Only-123!';
EXEC(N'CREATE SCHEMA [Security]');
CREATE TABLE [Security].[WorkbenchRestorePending] ([Id] tinyint PRIMARY KEY, [IsPending] bit NOT NULL);
INSERT INTO [Security].[WorkbenchRestorePending] VALUES (1, 0);
CREATE TABLE dbo.HistoricalAuthority (Id int);
INSERT INTO dbo.HistoricalAuthority VALUES (1);
GRANT SELECT ON dbo.HistoricalAuthority TO web_probe;
BACKUP DATABASE RestoreProof TO DISK = '/tmp/restore-proof.bak' WITH INIT;
'@ 'RestoreProof'
    $connectionFile = Join-Path $testRoot 'connection.txt'
    [IO.File]::WriteAllText($connectionFile, "Server=localhost;Database=master;User ID=sa;Password=$($env:MSSQL_SA_PASSWORD)")

    foreach ($fault in @('marker', 'missing-proof', 'success')) {
        $global:proofFault = $fault
        $global:proofCalls = 0
        # Run the real script's SQL against the private server, injecting failure at its boundaries.
        function global:sqlcmd {
            $global:proofCalls++
            $query = [string]$args[[Array]::IndexOf($args, '-Q') + 1]
            if ($global:proofFault -eq 'marker' -and $global:proofCalls -eq 1) {
                $query = $query.Replace('USE [RestoreProof];', "THROW 51001, 'Injected marker failure', 1;`nUSE [RestoreProof];")
            }
            if ($global:proofFault -eq 'missing-proof' -and $global:proofCalls -eq 2) {
                $null = Invoke-TestSql 'DELETE FROM [Security].[WorkbenchRestorePending];' 'RestoreProof'
            }
            & $Docker exec --env SQLCMDPASSWORD $container /opt/mssql-tools18/bin/sqlcmd `
                -S localhost -U sa -d master -C -b -Q $query | Out-Null
            $global:LASTEXITCODE = $LASTEXITCODE
        }
        # WHEN restoration succeeds or a post-recovery marker boundary fails.
        $failure = $null
        try {
            & $restoreScript -ConnectionFile $connectionFile -Database RestoreProof `
                -Source '/tmp/restore-proof.bak' -Confirmation 'RESTORE RestoreProof'
        }
        catch { $failure = $_ }
        # THEN failures cannot restore ordinary access; successful restoration preserves the pending guard.
        $mode = Invoke-TestSql "SELECT user_access_desc FROM sys.databases WHERE name = 'RestoreProof';"
        if ($fault -eq 'success') {
            if ($failure -or $mode -ne 'MULTI_USER' -or
                (Invoke-TestSql 'SELECT IsPending FROM [Security].[WorkbenchRestorePending] WHERE Id = 1;' 'RestoreProof') -ne '1') {
                throw 'Successful restore did not preserve the guarded migration/sanitation workflow.'
            }
        }
        elseif (-not $failure -or $mode -ne 'RESTRICTED_USER') { throw "Failed restore exposed ordinary access: fault=$fault mode=$mode error=$($failure.Exception.Message)" }

        $env:SQLCMDPASSWORD = 'Disposable-Probe-Only-123!'
        $probe = & $Docker exec --env SQLCMDPASSWORD $container /opt/mssql-tools18/bin/sqlcmd `
            -S localhost -U web_probe -d RestoreProof -C -b -Q 'SELECT * FROM dbo.HistoricalAuthority;' 2>&1
        $probeExit = $LASTEXITCODE
        $env:SQLCMDPASSWORD = $env:MSSQL_SA_PASSWORD
        if (($fault -eq 'success' -and $probeExit -ne 0) -or ($fault -ne 'success' -and $probeExit -eq 0)) {
            throw 'Ordinary database access did not match the restore outcome.'
        }
        Write-Host "SQL restore scenario '$fault' passed."
    }
}
finally {
    Remove-Item Function:\sqlcmd -ErrorAction SilentlyContinue
    Remove-Variable proofFault, proofCalls -Scope Global -ErrorAction SilentlyContinue
    if ($started) { & $Docker rm --force $container | Out-Null }
    $env:SQLCMDPASSWORD = $previousSqlPassword
    $env:MSSQL_SA_PASSWORD = $previousSaPassword
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
