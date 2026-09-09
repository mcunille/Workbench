# Azure release, public access, and cleanup

Use this procedure for an existing installation provisioned through the
[Azure deployment runbook](azure-deployment.md). Each Azure mutation requires operator approval
of its exact scope. A preview, successful upload, or healthy candidate does not authorize a
traffic switch. Keep environment parameters and evidence outside Git; never export secret values.

## Prepare the complete release

Record the subscription, resource group, app/job names, serving revision and digest, source commit,
installed schema, current traffic, ingress policy, and managed certificate binding. Use the
nonsecret parameter file for this installation; preserve all identities and service endpoints.
Verify retained SQL/Blob backups and independently decryptable recovery keys before migration.

```powershell
# Set these to the recorded source revisions, not mutable tags.
git diff --name-status $deployedCommit $candidateCommit
git diff $deployedCommit $candidateCommit -- src/Workbench.Server/Persistence/Migrations
az containerapp show -g $group -n $app --query 'properties.configuration.ingress'
az containerapp revision list -g $group -n $app --query '[].{name:name,active:properties.active,health:properties.healthState,traffic:properties.trafficWeight}'
```

Compare the entire deployed-to-candidate range, not just the last PR. During the first security
rollout, the PR had no migration but the release included `AddItemRestoration`; the candidate
correctly remained unready until that migration ran. Review old-web/new-schema and worker
compatibility before deciding whether workers may continue. If compatibility requires a pause,
obtain approval for the interruption; do not silently stop the public service.

Build the selected reviewed source, label its commit, run repository release gates, and perform
the [exact-image assessment](azure-security-controls.md#exact-image-assessment). After upload
approval, push and read back the registry digest. Store `repository@sha256:...` in the installation
parameters. Never substitute the latest main commit or a mutable image tag after verification.

## Stage without switching traffic

Prepare a module parameter file containing the inputs of `infra/azure/modules/workloads.bicep`.
Set `image` to the verified digest and `releaseTraffic` to the current explicit revision at 100%.
Preserve `ingressPolicy`, certificate, endpoints, identities, secrets references, and worker schedule.
The shared image parameter updates web, worker, and migration jobs: the worker's next scheduled
execution uses the new image even while web traffic remains on the old revision.

```powershell
az bicep build --file infra/azure/modules/workloads.bicep --outfile $compiledWorkloads
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
az deployment group what-if -g $group --template-file $compiledWorkloads --parameters "@$workloadParameters"
if ($LASTEXITCODE -ne 0) { throw 'Preview failed.' }
# Only after approval of the reviewed preview:
az deployment group create -g $group -n $deploymentName --template-file $compiledWorkloads --parameters "@$workloadParameters" --query properties.provisioningState
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed; inspect operations before retrying.' }
```

Read back Succeeded, exact images, existing traffic, HTTPS-only ingress and the intended allowlist.
Inspect probe differences by probe type; Azure can reorder the array. Do not dismiss unexplained
changes as preview noise or use a full deployment for an approval limited to one ingress property.

When migration is required and approved, start the manual job once:

```powershell
$execution = az containerapp job start -g $group -n $migrationJob --query name -o tsv
if ($LASTEXITCODE -ne 0) { throw 'Migration start failed.' }
az containerapp job execution show -g $group -n $migrationJob --job-execution-name $execution --query 'properties.{status:status,start:startTime,end:endTime}'
```

Repeat the read-only status check until terminal; do not issue another start while it runs. Require
Succeeded and the matching execution's `Database 'Workbench' migrated successfully.` console line.
Query `ContainerAppConsoleLogs_CL` by `ContainerGroupName_s startswith '<execution>-'`; job console
logs can lag ARM status. Azure Run Command's outer "Enable succeeded" is not a guest exit check.
Keep failed-job diagnostics private and never dump mounted connection files.

Read candidate revision health and replica readiness. A 503 is a failed gate, not permission to
bypass readiness or switch traffic. Diagnose schema, SQL, storage and provider checks. Verify a
scheduled worker execution uses the expected image and completes; inspect queue status separately.

## Promote and verify

After candidate checks and separate traffic approval:

```powershell
az containerapp ingress traffic set -g $group -n $app --revision-weight "${candidateRevision}=100"
if ($LASTEXITCODE -ne 0) { throw 'Traffic change failed; inspect current allocation.' }
az containerapp show -g $group -n $app --query 'properties.configuration.ingress.traffic'
Invoke-WebRequest "$publicOrigin/health/ready" -TimeoutSec 60
Invoke-WebRequest "$publicOrigin/" -TimeoutSec 60
Invoke-WebRequest "$publicOrigin/api/auth/me" -SkipHttpErrorCheck -TimeoutSec 60
```

Require 200 for readiness/home, 401 for unauthenticated identity access, and the five
[browser security headers](browser-security.md). Confirm real sign-in, worker success, and retained
IP policy. Save the new explicit traffic allocation in the authoritative installation parameters
immediately so a later deployment does not send traffic back. Keep the prior compatible revision
for rollback. A rollback needs approval and installed-schema compatibility; do not down-migrate
automatically. Rollback retention alone is not an exercised rollback test.

## Exercise a compatible rollback

Obtain approval for the target revision, temporary traffic transfer, validation, and return to the
current release. Compare the complete source range and installed schema first; check compatibility
with workers that will continue running. Never automatically down-migrate the database.

Read the retained revision's immutable image, active state, FQDN and replica readiness. A revision
that previously reported Healthy but has zero replicas is not yet a warm rollback target. A request
to its revision-specific HTTPS hostname can wake it; canonical-host validation may return 400.
That response alone is not a successful readiness check. Require ready replicas and then validate
through the canonical production hostname after the approved traffic switch.

Use `az containerapp ingress traffic set` as above with the explicit retained revision. Read back
100% traffic and verify homepage, readiness, anonymous identity rejection, security headers, assets,
and actual sign-in. Return to the current revision under the same approved drill scope, repeat the
checks, and confirm the expected asset and traffic allocation. If checks fail, restore the known
working revision and investigate before proceeding. Save nonsecret timestamps and results.

The [2026-09-09 acceptance record](deployment-verification.md#azure-acceptance-follow-up-2026-09-09-utc)
documents a completed frontend-only release rollback and return; it does not prove compatibility
across an arbitrary future database migration.

## Open public access separately

Once sign-in and launch checks pass, obtain approval to change `ingressPolicy` to
`{ "mode": "Public", "allowCidrs": [] }`. Keep external HTTPS ingress, certificate binding and
traffic intact. Preview the normal template. For an ingress-only approval, the following narrow
ARM PATCH avoids unrelated template/default changes. `$appId` must be the verified full ARM ID
and `$patchFile` an operator-owned nonsecret file outside Git:

```powershell
@{ properties = @{ configuration = @{ ingress = @{ ipSecurityRestrictions = @() } } } } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $patchFile
az rest --method patch --url "https://management.azure.com${appId}?api-version=2025-01-01" --body "@$patchFile" --output none
if ($LASTEXITCODE -ne 0) { throw 'Ingress update failed.' }
az containerapp show -g $group -n $app --query 'properties.{state:provisioningState,ingress:configuration.ingress}'
```

Wait for Succeeded; require empty restrictions, `external=true`, `allowInsecure=false`, unchanged
certificate and traffic. Repeat HTTP checks and verify SQL, Blob and Key Vault still report public
network access Disabled. Persist Public mode in the installation parameters as well as the live
resource. This opens web reachability; authentication and tenant authorization remain mandatory.

## Cleanup after verification

Inventory before proposing exact deletions. Check restore jobs are terminal, the disposable target
is distinct from the protected backup source, and no private endpoints or workloads depend on it.
Record restore evidence and verify recovery keys before deleting test data. Remove the target's
temporary backup-role assignment and then the approved target account. Do not delete the vault,
policy, backup instance, recovery points or production storage.

List current custom-domain bindings before deleting an uploaded bootstrap certificate; retain the
bound managed certificate. Deactivate only an approved zero-traffic obsolete revision, retaining
the immediately previous compatible release. Retain ACR images until image cleanup is separately
scoped. Preserve the Container Apps managed resource group and Network Watcher.

Read back deletions, traffic, HTTPS readiness and backup `ProtectionConfigured`. The completed
2026-09-08 cleanup is recorded in [deployment verification](deployment-verification.md#azure-public-launch-2026-09-08-utc).
