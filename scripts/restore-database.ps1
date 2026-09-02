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
    & $sqlcmd.Source -S $builder.DataSource -U $builder.UserID -d master -C -b -Q `
        "ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [$Database] FROM DISK = N'$escapedSource' WITH REPLACE, RECOVERY; ALTER DATABASE [$Database] SET MULTI_USER"
    if ($LASTEXITCODE -ne 0) { throw 'Database restore failed.' }
    Write-Host "Database '$Database' restored. Run migrations, restore sanitize, and all security probes before cutover."
}
finally {
    $env:SQLCMDPASSWORD = $previousPassword
}
