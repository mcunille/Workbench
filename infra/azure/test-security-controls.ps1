# Copyright (c) 2026 The White Stag Collection.
param([Parameter(Mandatory)][string] $TemplateFile, [Parameter(Mandatory)][string] $FoundationTemplateFile)
$ErrorActionPreference = 'Stop'

function Assert-Control([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

# GIVEN compiled security controls that operate on existing production resources.
$template = Get-Content -LiteralPath $TemplateFile -Raw | ConvertFrom-Json -AsHashtable
$foundation = Get-Content -LiteralPath $FoundationTemplateFile -Raw | ConvertFrom-Json -AsHashtable
$resources = @($template.resources)
$diagnostics = @($resources | Where-Object type -eq 'Microsoft.Insights/diagnosticSettings')
# WHEN the controls are deployed, each service must have explicit audit collection.
# THEN SQL captures authentication, never general batch/query text containing application secrets.
$audit = @($resources | Where-Object type -eq 'Microsoft.Sql/servers/auditingSettings')
Assert-Control ($audit.Count -eq 1 -and $audit[0].properties.state -eq 'Enabled') 'SQL auditing must be enabled.'
$groups = @($audit[0].properties.auditActionsAndGroups)
Assert-Control ($groups.Count -eq 2 -and $groups -contains 'SUCCESSFUL_DATABASE_AUTHENTICATION_GROUP' -and $groups -contains 'FAILED_DATABASE_AUTHENTICATION_GROUP') 'Only SQL authentication audit groups are permitted.'
Assert-Control ($audit[0].properties.isAzureMonitorTargetEnabled -eq $true) 'SQL audit must route to Azure Monitor.'
$categories = @($diagnostics | ForEach-Object { $_.properties.logs } | ForEach-Object { $_.category })
foreach ($category in @('SQLSecurityAuditEvents','AuditEvent','StorageRead','StorageWrite','StorageDelete','ContainerRegistryLoginEvents','ContainerRegistryRepositoryEvents','CoreAzureBackup','AddonAzureBackupJobs')) {
    Assert-Control ($categories -contains $category) "Missing security category: $category"
}
foreach ($diagnostic in $diagnostics) {
    Assert-Control (-not [string]::IsNullOrEmpty($diagnostic.properties.workspaceId)) 'Diagnostics must route to the central workspace.'
    Assert-Control (@($diagnostic.properties.logs | Where-Object enabled -ne $true).Count -eq 0) 'Security categories must be enabled.'
}
# AND service diagnostic tables have an explicit 30-day default without paid archive retention.
Assert-Control ($template.parameters.logRetentionDays.defaultValue -eq 30) 'Security resource-log retention defaults to 30 days.'
$tables = @($resources | Where-Object type -eq 'Microsoft.OperationalInsights/workspaces/tables')
Assert-Control ($tables.Count -gt 0) 'Security log tables need explicit retention.'
Assert-Control ($template.parameters.retentionTableNames.defaultValue.Count -eq 0 -and $tables[0].copy.count -eq "[length(parameters('retentionTableNames'))]") 'First setup must not attempt to create service-owned tables before ingestion.'
$workspace = @($foundation.resources | Where-Object type -eq 'Microsoft.OperationalInsights/workspaces')
Assert-Control ($foundation.parameters.logRetentionDays.defaultValue -eq 30 -and $workspace[0].properties.retentionInDays -eq "[parameters('logRetentionDays')]") 'First ingestion must inherit 30-day workspace retention.'
foreach ($table in $tables) {
    Assert-Control ($table.properties.retentionInDays -eq "[parameters('logRetentionDays')]" -and $table.properties.totalRetentionInDays -eq "[parameters('logRetentionDays')]") 'Tables must not silently extend retention.'
}
# AND SQL has a server-deletion recovery window and an accidental-deletion lock.
$server = @($foundation.resources | Where-Object type -eq 'Microsoft.Sql/servers')
Assert-Control ($server.Count -eq 1 -and $server[0].properties.retentionDays -eq 7) 'SQL server soft delete must retain seven days.'
$locks = @($resources | Where-Object type -eq 'Microsoft.Authorization/locks')
Assert-Control ($locks.Count -ge 2 -and @($locks | Where-Object { $_.properties.level -ne 'CanNotDelete' }).Count -eq 0) 'SQL and native backup vault require deletion locks, not read-only locks.'
# AND administrative alerting is independent of workspace ingestion, and native failures have a recipient.
$admin = @($resources | Where-Object type -eq 'Microsoft.Insights/activityLogAlerts')
Assert-Control ($admin.Count -eq 1 -and $admin[0].properties.enabled -eq $true) 'Direct administrative alerts must be enabled.'
$operationCondition = @($admin[0].properties.condition.allOf | Where-Object { $_.ContainsKey('copy') })
$operationLoop = $operationCondition[0]['copy'][0]
Assert-Control ($operationLoop.input.field -eq 'operationName' -and $operationLoop.input.equals -match "variables\('([^']+)'\)") 'Administrative operations must reach the alert condition.'
$operations = $template.variables[$Matches[1]]
foreach ($operation in @('Microsoft.Authorization/roleAssignments/write','Microsoft.Insights/diagnosticSettings/delete','Microsoft.Sql/servers/delete','Microsoft.DataProtection/backupVaults/backupPolicies/write')) {
    Assert-Control ($operations -contains $operation) "Missing administrative alert: $operation"
}
$notification = @($resources | Where-Object type -eq 'Microsoft.AlertsManagement/actionRules')
Assert-Control ($notification.Count -eq 1 -and $notification[0].properties.enabled -eq $true -and $notification[0].properties.actions[0].actionType -eq 'AddActionGroups') 'Native backup alerts must reach the operations action group.'
Write-Host 'Security diagnostic, retention, and deletion-protection contracts passed.'
