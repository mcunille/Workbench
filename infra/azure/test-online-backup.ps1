[CmdletBinding()]
param([Parameter(Mandatory)][string]$TemplateFile)
$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath $TemplateFile -Raw | ConvertFrom-Json
$storage = @($template.resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts')
if ($storage.Count -ne 1 -or $storage[0].properties.publicNetworkAccess -ne 'Disabled' -or
    $storage[0].properties.allowSharedKeyAccess -ne $false -or $storage[0].sku.name -ne 'Standard_GRS') {
    throw 'Backup storage must be separate, private, keyless and geographically redundant.'
}
$job = @($template.resources | Where-Object type -eq 'Microsoft.App/jobs')
if ($job.Count -ne 1 -or ($job[0].properties.template.containers[0].args -join ' ') -ne 'backup capture') {
    throw 'Expected a dedicated capture job, not a restore or workload-control command.'
}
$roles = @($template.resources | Where-Object type -eq 'Microsoft.Authorization/roleDefinitions')
if ($roles.Count -ne 1) { throw 'Expected a scoped backup writer role.' }
$actions = @($roles[0].properties.permissions[0].dataActions)
if ($actions.Count -ne 2 -or ($actions | Where-Object { $_ -match 'delete|\*' })) { throw 'Collector must not delete data.' }
$policies = @($template.resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies')
if ($policies.Count -ne 1 -or $policies[0].properties.immutabilityPeriodSinceCreationInDays -lt 37) { throw 'Backup retention protection is missing.' }
if ($template.parameters.enableSchedule.defaultValue -ne $false) { throw 'Schedule must require deliberate activation after protection verification.' }
$alert = @($template.resources | Where-Object type -eq 'Microsoft.Insights/scheduledQueryRules')
if ($alert.Count -ne 1 -or $alert[0].properties.criteria.allOf[0].query.Contains('${prefix}')) { throw 'Backup alert must bind the actual job name.' }
'ONLINE_BACKUP_TEMPLATE_CHECKS_PASSED'
