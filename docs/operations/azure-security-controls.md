# Azure security controls

Apply these controls before unrestricted ingress, in addition to the [ingress policy](azure-ingress.md)
and [browser protections](browser-security.md). Deployments and irreversible backup changes require
an explicit review and approval. These templates do not enable public access or change workload images.

## Resource audit and notification configuration

`infra/azure/security-controls.bicep` targets existing resources in one resource group. Supply
`prefix`, `sqlServerName`, `storageName`, `vaultName`, `registryName`, `logsName`, `actionGroupId`,
and the native `backupVaultName` (omit only before native backup exists). `databaseName` defaults
to `Workbench`. Store these nonsecret values in an ARM parameter file outside Git.

```powershell
az bicep build --file infra/azure/security-controls.bicep --outfile <compiled-security.json>
az deployment group what-if --resource-group <group> --template-file <compiled-security.json> --parameters '@<security.parameters.json>'
# After approval of that exact preview:
az deployment group create --resource-group <group> --name security-controls --mode Incremental --template-file <compiled-security.json> --parameters '@<security.parameters.json>'
```

The template collects SQL authentication auditing, Key Vault audit events, Blob read/write/delete
operations, registry login/repository events, and native backup job/policy/protection events.
SQL batch/query auditing is intentionally excluded: application operations can contain recovery
tokens, password-related values, and tenant data. Resource logs contain identifiers, IPs and paths;
restrict workspace access to operators. Do not add workloads to workspace Reader roles.

Resource diagnostic tables use **30 days** for both interactive and total retention; no archive tier
is configured. First confirm the workspace's default is 30 days. Leave `retentionTableNames` empty
on first setup: AzureDiagnostics and other service-owned tables must be created by ingestion, not
by this template. After observing ingestion, rerun the approved template with `retentionTableNames`
containing only those existing tables to enforce explicit 30-day overrides. Check for preexisting
overrides before launch; defaults alone do not replace those overrides.
The existing workspace daily cap is shared by application and security logs and can
interrupt ingestion. More logs can increase ingestion charges; inspect Usage daily after enabling
and retain the existing budget alerts. A successful diagnostic-settings deployment proves configuration,
not event delivery. Do not enable paid Defender plans or upgrade the registry SKU implicitly.

Administrative-change alerts evaluate the Activity Log directly, independent of the ingestion cap.
The operation list is in `infra/azure/security-operations.json` and intentionally includes approved
deployments as well as suspicious changes. It covers the whole subscription, including subscription
RBAC changes; use this scope only for the dedicated Workbench subscription. The native backup alert
processing rule routes built-in vault alerts to the existing operations action group.

Export the subscription audit trail separately with `infra/azure/security-activity-log.bicep`:

```powershell
az deployment sub what-if --location <region> --template-file infra/azure/security-activity-log.bicep --parameters diagnosticName=<prefix>-security workspaceId=<workspace-resource-id>
# After approval of that exact preview:
az deployment sub create --location <region> --name security-activity-log --template-file infra/azure/security-activity-log.bicep --parameters diagnosticName=<prefix>-security workspaceId=<workspace-resource-id>
```

Azure's built-in Activity Log/`AzureActivity` retention is **90 days**, a platform exception to the
30-day application/resource-log policy; this procedure does not add a paid archive or extend it.
The workspace is not an immutable archive and a sufficiently privileged subscription administrator
can remove logging or alerts. Independent administration or archival protection is a separate design.

After deployment, list each resource's diagnostic settings, verify the audit groups and table retention,
and query recent `AZKVAuditLogs`, `StorageBlobLogs`, `AzureDiagnostics` (SQLSecurityAuditEvents),
`ContainerRegistryLoginEvents`, `ContainerRegistryRepositoryEvents`, `AddonAzureBackupJobs`, and
`AzureActivity`. Use ordinary approved workload activity; do not inject failures into production.
Verify the action group recipient, backup rule scope, and delivered alert for an approved administrative
change. Missing data is a failed acceptance gate even if ARM reports Succeeded. Review the rules in
the portal if diagnostics or the workspace itself are unavailable.

## Deletion and recovery safeguards

The foundation declares seven-day SQL logical-server soft delete, separately from seven-day database
PITR/Geo redundancy. For an existing server, an approved scoped change can use:

```powershell
az sql server update --resource-group <group> --name <server> --soft-delete-retention-days 7
az sql server show --resource-group <group> --name <server> --query retentionDays
```

Logical-server recovery is a preview Azure feature. Do not describe it as a tested recovery procedure
without a separate isolated drill. No long-term retention policy is required by this seven-day design.
The security template adds `CanNotDelete` locks to SQL and the native backup vault. SQL's lock also
protects child databases; an approved cleanup may require temporarily removing it. These locks do
not block reads or normal database writes, but an Owner can remove them. Never use `ReadOnly` here.

For native vaulted backups, review the live vault's `securitySettings`, every protected instance and
policy before proposing irreversible settings. `Unlocked` immutability is enabled but reversible;
`Locked` prevents disabling it. `AlwaysOn` soft delete prevents disabling soft delete. Keep the existing
14-day soft-delete safety window and seven-day backup retention distinct. Locking does not authorize
retention increases, a new backup system, or automated recovery. Confirm workload/region support and
read back the applied state; do not claim physical WORM or customer-initiated Blob cross-region restore
solely from the vault's lock or GRS setting. Resource Guard/MUA needs an independently controlled
administrator and is not silently provisioned into the same owner's control.

After explicit approval to make **both** settings irreversible, prepare this nonsecret PATCH body
outside Git and inspect it together with the vault ID:

```json
{
  "properties": {
    "securitySettings": {
      "immutabilitySettings": { "state": "Locked" },
      "softDeleteSettings": { "state": "AlwaysOn", "retentionDurationInDays": 14 }
    }
  }
}
```

```powershell
# $vaultId is the previously verified native Backup vault ARM ID.
az rest --method patch --url "${vaultId}?api-version=2025-07-01" --body '@<reviewed-protection.json>' --output none
if ($LASTEXITCODE -ne 0) { throw 'Vault protection update failed; do not change the backup policy to work around it.' }
az rest --method get --url "${vaultId}?api-version=2025-07-01" --query 'properties.{state:provisioningState,security:securitySettings,storage:storageSettings}'
```

Require Succeeded, Locked, AlwaysOn, retained soft-delete duration and GeoRedundant storage, then
read back the existing backup policy and its seven-day VaultStore retention. A 202 response alone
is not completion. Do not test protection by attempting to delete production recovery points.

## Exact-image assessment

Before a release, inspect the selected `repository@sha256:...`, save that exact local image with
`docker image save --output <archive.tar> <repository@sha256:digest>`, and scan the archive locally
with a version-pinned, checksum-verified official Trivy release:

```powershell
trivy image --input <archive.tar> --scanners vuln --format json --output <report.json>
if ($LASTEXITCODE -ne 0) { throw 'Image assessment failed.' }
```

Retain scanner version/checksum, vulnerability-database metadata, image manifest/config digests,
archive/report SHA256s, and UTC time outside Git. Review **all severities**, including unfixed findings;
exit code zero is scan completion, not approval. High/Critical findings block release pending a verified
fix or explicit risk decision. Medium/Low advisories still need documented disposition. Never use
`--ignore-unfixed` to conceal findings. A report covers detected OS/.NET components, not an application
penetration test or bundled/minified browser dependencies. Scan each new candidate digest again.

The 2026-09-08 assessment of the deployed release found no High/Critical or detected .NET advisories,
but did find Medium/Low Ubuntu advisories, including OpenSSL fixes absent from the then-current
Microsoft chiseled runtime. Refreshing that same tag did not fix them. Track upstream replacement
and reassess applicability before public cutover; do not copy arbitrary system libraries into the runtime
or claim a clean image. Signatures, SBOM enforcement and paid registry features remain separately scoped.

References: [SQL server recovery](https://learn.microsoft.com/azure/azure-sql/database/deleted-logical-server-restore),
[SQL auditing](https://learn.microsoft.com/azure/azure-sql/database/auditing-setup),
[immutable vaults](https://learn.microsoft.com/azure/backup/backup-azure-immutable-vault-concept),
[Backup monitoring](https://learn.microsoft.com/azure/backup/monitor-backup-reference),
[Trivy image input](https://trivy.dev/docs/latest/guide/references/configuration/cli/trivy/).
