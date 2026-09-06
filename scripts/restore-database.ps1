[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConnectionFile,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Confirmation
)

$ErrorActionPreference = 'Stop'
if ($Database -notmatch '^[A-Za-z][A-Za-z0-9_]{0,127}$' -or $Confirmation -cne "RESTORE $Database") {
    throw "Refusing restore: use an identifier-safe database and -Confirmation 'RESTORE $Database'."
}
if (-not (Test-Path -LiteralPath $ConnectionFile -PathType Leaf) -or [string]::IsNullOrWhiteSpace($Source)) {
    throw 'An explicit connection file and SQL Server-visible backup source are required.'
}
$sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
if (-not $sqlcmd) { throw 'Microsoft sqlcmd is required for restore operations.' }
$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new((Get-Content -LiteralPath $ConnectionFile -Raw).Trim())
if ($builder.InitialCatalog -ne 'master') {
    throw 'Restore credentials must explicitly target the master database.'
}
$escapedSource = $Source.Replace("'", "''", [StringComparison]::Ordinal)
$previousPassword = $env:SQLCMDPASSWORD
try {
    $env:SQLCMDPASSWORD = $builder.Password
    $restoreSql = @"
        ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        RESTORE DATABASE [$Database] FROM DISK = N'$escapedSource' WITH REPLACE, RECOVERY, RESTRICTED_USER;
        USE [$Database];
        IF SCHEMA_ID(N'Security') IS NULL EXEC(N'CREATE SCHEMA [Security]');
        IF OBJECT_ID(N'[Security].[WorkbenchRestorePending]', N'U') IS NULL
        BEGIN
            CREATE TABLE [Security].[WorkbenchRestorePending]
            (
                [Id] tinyint NOT NULL CONSTRAINT [PK_WorkbenchRestorePending] PRIMARY KEY,
                [IsPending] bit NOT NULL,
                CONSTRAINT [CK_WorkbenchRestorePending_Singleton] CHECK ([Id] = 1)
            );
        END;
        IF EXISTS (SELECT 1 FROM [Security].[WorkbenchRestorePending] WHERE [Id] = 1)
            UPDATE [Security].[WorkbenchRestorePending] SET [IsPending] = 1 WHERE [Id] = 1;
        ELSE
            INSERT INTO [Security].[WorkbenchRestorePending] ([Id], [IsPending]) VALUES (1, 1);
"@
    & $sqlcmd -S $builder.DataSource -U $builder.UserID -d master -N -b -Q $restoreSql
    if ($LASTEXITCODE -ne 0) {
        throw 'Database restore failed; access was not released. Keep traffic stopped and recover with a privileged connection.'
    }

    # A failed restore or marker write must never reopen backup-era authentication state.
    # Verify the committed guard on a separate connection before releasing restricted access.
    $releaseSql = @"
        USE [$Database];
        IF NOT EXISTS (SELECT 1 FROM [Security].[WorkbenchRestorePending] WHERE [Id] = 1 AND [IsPending] = 1)
            THROW 51000, 'Restore pending marker is not established; access remains restricted.', 1;
        USE [master];
        ALTER DATABASE [$Database] SET MULTI_USER WITH ROLLBACK IMMEDIATE;
"@
    & $sqlcmd -S $builder.DataSource -U $builder.UserID -d master -N -b -Q $releaseSql
    if ($LASTEXITCODE -ne 0) {
        throw 'Database restore release failed; keep traffic stopped and inspect the pending marker and access mode.'
    }
}
finally {
    $env:SQLCMDPASSWORD = $previousPassword
}

Write-Host "Database '$Database' restored. Run migrations, restore sanitize, and all security probes before cutover."
