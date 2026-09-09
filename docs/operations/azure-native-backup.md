# Native Azure Blob backup setup

This is the selected backup path for the recorded Azure installation. Do not deploy
`infra/azure/backup.bicep` for this choice: that is the separate custom archive implementation.
Native backup setup currently uses the portal procedure below, followed by ARM readback; it is
not created by `main.bicep`. Every create, grant and protection change needs operator approval.
Normal backups leave the application running. Recovery remains manual.

## Record the configuration

Keep these nonsecret inputs in the installation's protected operations directory: subscription,
resource group, source storage account ID, container (`workbench`), vault name, region, policy name,
daily schedule (03:00 UTC), retention (seven days), redundancy (GeoRedundant), and action group ID.
Use the same region as the source for the vault; geographic redundancy supplies the regional copy,
not a second independently deployed vault. For the accepted West US 2 installation the paired region
is West Central US. Do not equate a GRS source account with a completed vaulted backup or automatic
cross-region application failover. Confirm current region/workload support before approval.

## Create and protect

1. In the subscription's **Resource providers**, register `Microsoft.DataProtection` if needed and
   wait for Registered. In **Create a resource**, choose **Backup vault** (not Recovery Services
   vault). Select the recorded group/name/source region and **Geo-redundant** storage. Enable its
   system-assigned identity. Review and create; retain the vault ARM ID and principal ID.
2. On the source storage account's **Access control (IAM)**, grant that vault identity
   **Storage Account Backup Contributor**, scoped only to this account. Record the assignment ID.
   Do not grant the web/worker identities access to the backup vault or add subscription-wide roles.
3. In the vault, choose **Backup policies → Add**, data source **Azure Blobs**. Select **Vaulted**
   protection, daily at **03:00 UTC**, retain daily points **7 days**. Do not add weekly/monthly/yearly
   retention or the optional operational tier for this policy. Record the policy ID and start date.
4. Choose **Backup / Configure protection**, data source **Azure Blobs**, this vault and policy.
   Select the source account and explicitly browse/select the `workbench` container. Do not select
   all future containers. Revalidate permissions after propagation, inspect the selected scope,
   and configure. Require the instance to reach `ProtectionConfigured`.

Portal labels can change; Microsoft's [vaulted Blob quickstart](https://learn.microsoft.com/en-us/azure/backup/blob-backup-configure-quick)
describes the current selection flow and [configuration guide](https://learn.microsoft.com/en-us/azure/backup/blob-backup-configure-manage)
describes account-scoped backup authority. If seven-day VaultStore retention is rejected, stop and
review the policy rather than silently selecting a longer paid duration.

Preserve source storage `publicNetworkAccess=Disabled`, shared-key access false, default network
action Deny and bypass None. The recorded installation completed its scheduled backup with these
settings and no resource-access exceptions. A readiness error is not authorization to open storage
or enable a broad trusted-services bypass. Investigate the exact service validation error.

## Read back before acceptance

Set `$vaultId` to the recorded full ARM ID (starting `/subscriptions/`), `$group` and `$storage` to
the selected source. These queries contain no secret values:

```powershell
$vaultUrl = "https://management.azure.com${vaultId}"
az rest --method get --url "${vaultUrl}?api-version=2025-07-01" --query 'properties.{state:provisioningState,storage:storageSettings,security:securitySettings}'
az rest --method get --url "${vaultUrl}/backupPolicies?api-version=2025-07-01" --query 'value[].{id:id,rules:properties.policyRules}'
az rest --method get --url "${vaultUrl}/backupInstances?api-version=2025-07-01" --query 'value[].{id:id,properties:properties}'
az storage account show -g $group -n $storage --query '{publicNetworkAccess:publicNetworkAccess,sharedKey:allowSharedKeyAccess,network:networkRuleSet}'
```

Require GeoRedundant, `VaultStore` with `deleteAfter.duration=P7D`, a daily UTC interval at the
selected time, the correct source/container/policy IDs and configured protection. Save metadata
outside Git. Do not change source replication or version-retention settings while configuring this
separate policy. SQL retains its own seven-day Geo PITR policy; Blob backup does not back up SQL.

Apply the [security controls](azure-security-controls.md) with this `backupVaultName` to add vault
diagnostics, the backup alert route and deletion lock. Independently approve and apply the documented
irreversible Locked immutability and AlwaysOn 14-day soft-delete changes only after reviewing the
policy. Seven-day recovery-point retention and 14-day soft delete are different controls.

Run one approved **Backup now** from the protected instance, then observe the first scheduled job:

```powershell
az rest --method get --url "${vaultUrl}/backupJobs?api-version=2025-07-01" --query 'value[].{id:name,operation:properties.operation,status:properties.status,start:properties.startTime,end:properties.endTime}'
```

Require terminal Completed for the matching operation and an available recovery point in the portal.
Query `AddonAzureBackupJobs` in the configured workspace after ingestion; a successful creation or
`CoreAzureBackup` row is insufficient. Record job IDs, times, retention and readback. See the
[accepted installation evidence](deployment-verification.md#azure-acceptance-follow-up-2026-09-09-utc).

## Recovery handoff

Choose the protected instance's **Restore** action and a retained recovery point. Target an isolated
account with no conflicting container names; approve that account, network and temporary permissions
separately. Never target writable production storage for a drill. After the native restore completes,
pair it with an isolated SQL PITR, apply restore guards/sanitation, then follow
[manual reconciliation](online-backup-recovery.md): omit custom catalogs only when the native-restored
bytes are already materialized. SQL owns the recovered references; log missing blobs and review
orphan deletion through the recovery plan. Retain independent certificate/proof recovery material.
Geographic storage redundancy alone does not prove customer-initiated cross-region restore support;
review the service's current restore capabilities for the incident before promising an RTO.
