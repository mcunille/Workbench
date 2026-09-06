# Copyright (c) 2026 The White Stag Collection.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/local-self-host/Configuration.ps1"
function Reject([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid setup input was accepted.' }
}
# GIVEN caller-provided tenant and email values, WHEN validated, THEN preserve names and reject malformed inputs.
$settings = Get-LocalSetupConfiguration @{ TenantName = "QA O'Brien"; AdminEmail = 'qa@example.test'; InstallationRoot = 'C:/QA' }
if ($settings.TenantName -cne "QA O'Brien") { throw 'Tenant name was changed.' }
Reject { Get-LocalSetupConfiguration @{TenantName=' '; AdminEmail='qa@example.test'} }
Reject { Get-LocalSetupConfiguration @{TenantName='QA'; AdminEmail='not an email'} }
Reject { Get-LocalSetupConfiguration @{TenantName='QA'; AdminEmail='qa@example.test'; Unexpected='value'} }
Reject { Get-LocalSetupConfiguration @{TenantName='QA'; AdminEmail='qa@example.test'; TrustLocalCertificate='false'} }
Reject { Get-LocalSetupConfiguration @{TenantName='QA'; AdminEmail='qa@example.test'; HttpPort=443; HttpsPort=443} }
Reject { Get-LocalSetupConfiguration @{TenantName='QA'; AdminEmail='qa@example.test'; HttpPort=0} }
Reject { Get-LocalSetupConfiguration @{TenantName='QA'; AdminEmail='qa@example.test'; HttpsPort=65536} }
# GIVEN Docker/Compose delimiter characters, WHEN selecting a root, THEN reject interpolation and mount injection.
foreach ($path in @('C:/bad,path', 'C:/bad$path', "C:/bad'path", "C:/bad`npath")) {
    Reject { Get-LocalSetupConfiguration @{TenantName='QA'; AdminEmail='qa@example.test'; InstallationRoot=$path} }
}
# GIVEN generated secrets, WHEN building connections, THEN quoting round-trips without relaxing TLS.
$connection = New-LocalConnection 'web' 'quote;"secret'
$builder = [System.Data.Common.DbConnectionStringBuilder]::new()
$builder.set_ConnectionString($connection)
if ($builder['Password'] -cne 'quote;"secret' -or $builder['TrustServerCertificate'] -ne 'False' -or $builder['Encrypt'] -ne 'True') { throw 'Connection security or quoting failed.' }
Write-Host 'Local self-host configuration checks passed.'
