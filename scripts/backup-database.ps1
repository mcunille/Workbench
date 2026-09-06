[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConnectionFile,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$Confirmation
)

$ErrorActionPreference = 'Stop'
if ($Database -notmatch '^[A-Za-z][A-Za-z0-9_]{0,127}$' -or $Confirmation -cne "BACKUP $Database") {
    throw "Refusing backup: use an identifier-safe database and -Confirmation 'BACKUP $Database'."
}
if (-not (Test-Path -LiteralPath $ConnectionFile -PathType Leaf) -or [string]::IsNullOrWhiteSpace($Destination)) {
    throw 'An explicit connection file and SQL Server-visible destination are required.'
}
$sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
if (-not $sqlcmd) { throw 'Microsoft sqlcmd is required for backup operations.' }
$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new((Get-Content -LiteralPath $ConnectionFile -Raw).Trim())
if ($builder.InitialCatalog -notin @('master', $Database)) {
    throw 'The connection file targets an unexpected database.'
}
$escapedDestination = $Destination.Replace("'", "''", [StringComparison]::Ordinal)
$previousPassword = $env:SQLCMDPASSWORD
try {
    $env:SQLCMDPASSWORD = $builder.Password
    & $sqlcmd -S $builder.DataSource -U $builder.UserID -d master -N -b -Q `
        "BACKUP DATABASE [$Database] TO DISK = N'$escapedDestination' WITH COPY_ONLY, CHECKSUM, INIT"
    if ($LASTEXITCODE -ne 0) { throw 'Database backup failed.' }
    Write-Host "Database '$Database' backup completed at the explicit server destination."
}
finally {
    $env:SQLCMDPASSWORD = $previousPassword
}
