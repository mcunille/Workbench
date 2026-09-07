# Copyright (c) 2026 The White Stag Collection.
param([Parameter(Mandatory)][string] $TemplateFile)
$ErrorActionPreference = 'Stop'

function Get-TemplateResources($Template) {
    foreach ($resource in $Template.resources) {
        $resource
        if ($resource.type -eq 'Microsoft.Resources/deployments' -and $resource.properties.template) {
            Get-TemplateResources $resource.properties.template
        }
    }
}

# GIVEN the compiled production deployment, including its nested foundation module.
$template = Get-Content -LiteralPath $TemplateFile -Raw | ConvertFrom-Json -AsHashtable
$resources = @(Get-TemplateResources $template)
$databases = @($resources | Where-Object type -eq 'Microsoft.Sql/servers/databases')
$accounts = @($resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts')

# WHEN Azure applies the database and application storage declarations.
# THEN both recovery stores retain geographic redundancy on subsequent deployments.
if ($databases.Count -ne 1 -or $accounts.Count -ne 1) {
    throw 'Expected one production database and one application storage account.'
}
$failures = @()
if ($databases[0].properties.requestedBackupStorageRedundancy -ne 'Geo') {
    $failures += 'SQL backups must retain Geo redundancy.'
}
if ($accounts[0].sku.name -ne 'Standard_GRS') {
    $failures += 'Application blobs must retain Standard_GRS redundancy.'
}
if ($failures.Count) { throw ($failures -join ' ') }
Write-Host 'Compiled deployment preserves SQL Geo and Blob GRS redundancy.'
