[CmdletBinding()]
param([Parameter(Mandatory)][string]$TemplateFile)
$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath $TemplateFile -Raw | ConvertFrom-Json
$storage = @($template.resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts')
if ($storage.Count -ne 1 -or $storage[0].properties.publicNetworkAccess -ne 'Disabled' -or
    $storage[0].properties.allowSharedKeyAccess -ne $false -or $storage[0].sku.name -ne 'Standard_GRS') {
    throw 'Backup storage must be separate, private, keyless and geographically redundant.'
}
if (@($template.resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts/managementPolicies').Count -ne 0) {
    throw 'Lifecycle deletion is unsupported for the immutable archive; use the separate expiration job.'
}
$jobs = @($template.resources | Where-Object type -eq 'Microsoft.App/jobs')
$capture = @($jobs | Where-Object { ($_.properties.template.containers[0].args -join ' ') -eq 'backup capture' })
$expiration = @($jobs | Where-Object { ($_.properties.template.containers[0].args -join ' ') -eq 'backup expire' })
if ($jobs.Count -ne 2 -or $capture.Count -ne 1 -or $expiration.Count -ne 1) { throw 'Capture and expiration require separate jobs/identities.' }
if (@($expiration[0].properties.template.containers[0].env | Where-Object { $_.name -match 'Source|Sql|Connection|Secret' }).Count -ne 0) {
    throw 'Expiration must not receive source, SQL or secret configuration.'
}
$roles = @($template.resources | Where-Object type -eq 'Microsoft.Authorization/roleDefinitions')
if ($roles.Count -ne 2) { throw 'Expected separate writer and expiration roles.' }
$writer = @($roles | Where-Object { $_.properties.permissions[0].dataActions -match '/write$' })
$expirer = @($roles | Where-Object { $_.properties.permissions[0].dataActions -match '/delete$' })
if ($writer.Count -ne 1 -or $expirer.Count -ne 1) { throw 'Separate read/write and read/delete roles are required.' }
foreach ($role in $roles) {
    $permissions = $role.properties.permissions[0]
    if (@($permissions.dataActions).Count -ne 2 -or @($permissions.actions).Count -ne 0 -or ($permissions.dataActions -match '\*')) { throw 'Backup roles must use exact blob-only operations.' }
}
if ($writer[0].properties.permissions[0].dataActions -match '/delete$' -or $expirer[0].properties.permissions[0].dataActions -match '/write$') { throw 'Collector and expiration authority must remain separate.' }
$policies = @($template.resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies')
if ($policies.Count -ne 1 -or $policies[0].properties.immutabilityPeriodSinceCreationInDays -lt 37) { throw 'Backup retention protection is missing.' }
if ($template.parameters.enableSchedule.defaultValue -ne $false -or $template.parameters.enableRetentionSchedule.defaultValue -ne $false) { throw 'Both schedules require deliberate activation after verification.' }
$alerts = @($template.resources | Where-Object type -eq 'Microsoft.Insights/scheduledQueryRules')
if ($alerts.Count -ne 2) { throw 'Capture and expiration both need status monitoring.' }
foreach ($alert in $alerts) {
    if ($alert.properties.criteria.allOf[0].query.Contains('${prefix}') -or $alert.properties.windowSize -ne 'P2D' -or
        -not $alert.properties.criteria.allOf[0].query.Contains('ago(26h)')) { throw 'Daily job status must bind the real job with runtime margin.' }
}
$expirationAssignments = @($template.resources | Where-Object { $_.type -eq 'Microsoft.Authorization/roleAssignments' -and $_.properties.principalId -match 'backup-retention' })
if ($expirationAssignments.Count -ne 2) { throw 'Expiration needs only archive metadata Reader and container read/delete.' }
foreach ($assignment in $expirationAssignments) {
    if ($assignment.scope -notmatch "format\('wbb" -or $assignment.scope -match 'sourceAccountName|Sql|resourceGroups') { throw 'Expiration authority must remain on the archive.' }
    if ($assignment.properties.roleDefinitionId -match 'backup-object-expirer' -and $assignment.scope -notmatch "'backups'") { throw 'Delete permission must be container-scoped.' }
}
'ONLINE_BACKUP_TEMPLATE_CHECKS_PASSED'
