# Private Azure bootstrap host and identity preparation

Use this route for a new installation after the inactive foundation deployment. It documents the
temporary Ubuntu host used by the accepted setup. An existing administrative host may replace it
only if the same private DNS, managed identities, tooling and cleanup checks pass. Do not run these
steps on an already initialized production database. Azure and Exchange mutations need explicit
approval of their resource/permission scope; generating local files is not deployment approval.

Keep a nonsecret installation worksheet outside Git containing subscription/group/location/prefix,
foundation outputs, registry and release digest, SQL administrator group ID, tenant name/admin email,
and all temporary resource and role-assignment IDs. Start by selecting the subscription and checking
provider registration for `Microsoft.App`, `Microsoft.Network`, `Microsoft.Sql`, `Microsoft.Storage`,
`Microsoft.KeyVault`, `Microsoft.ManagedIdentity`, `Microsoft.ContainerRegistry`, `Microsoft.Compute`
and `Microsoft.OperationalInsights`. Register missing providers only after approval; wait for Registered.

## Temporary host

The main template does not own this host. Use the Azure portal with the following reviewed values:

| Resource | Required configuration |
| --- | --- |
| Temporary resource group | Distinct from the production group; same region |
| NSG | Inbound rule Deny, any source/destination/protocol/port, priority 100 |
| Temporary subnet | An unused, nonoverlapping /24 in the production VNet (the recorded example used 10.42.3.0/24); attach the NSG |
| NAT gateway | Standard public IP and NAT in the temporary group; attach only to the temporary subnet |
| VM | Ubuntu 24.04 LTS x64 Gen2, reviewed available size/zone, Trusted Launch, Secure Boot/vTPM, SSH-key authentication, no public IP or inbound ports, temporary subnet, system-assigned identity |

Check quota and SKU restrictions before creating the VM; do not assume a B-series size is available.
The recorded installation used D2as_v6 in zone 1 after B-series restrictions. Record the resolved
image version and size. VM Run Command provides management access without opening SSH. The NAT IP
is for outbound connectivity, not a VM public address. See Microsoft's
[Linux Run Command procedure](https://learn.microsoft.com/en-us/azure/virtual-machines/linux/run-command).

Read back the NIC's private IP/no public IP, subnet/NSG/NAT and the VM identity. Through **VM → Run
command → RunShellScript**, run `getent ahostsv4` for the normal SQL, Blob and vault hostnames and
confirm the expected private endpoint IPs. Verify SQL TCP 1433 and HTTPS certificate validation to
Blob, vault and `login.microsoftonline.com`; a 401/403 after valid TLS is different from DNS/TLS failure.
Never use `curl -k`, disable SQL certificate validation, or open public database/vault access.

Install Docker, OpenSSL and Python 3 from the Ubuntu package repository. Install Azure CLI using
Microsoft's [Ubuntu installation instructions](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli-linux?pivots=apt)
and record the installed versions. Run Command scripts must start with `set -euo pipefail`, use LF
line endings and omit shell tracing. Require both a successful guest exit and the script's final
success marker; outer `Enable succeeded` alone is insufficient. Each Run Command invocation starts
a new shell: prefix every submitted script with the nonsecret variables it consumes, taken from the
worksheet. Variables and working directories do not carry between invocations. Never print or place
secret values in that prefix.

## Temporary authority and retained identities

After approval, add the VM system principal to the configured SQL setup-administrator Entra group.
Give it `AcrPull` on the selected registry and temporary `Key Vault Secrets Officer` on the vault.
Record these assignment IDs and the group membership for removal. The setup principal is powerful;
it must never become the runtime web/worker principal.

In the operator's local Azure CLI session, `$vmPrincipalId` comes from `az vm show`, `$setupGroupId`
from the worksheet, and `$registryId`/`$vaultId` from the selected resources. After approval:

```powershell
az ad group member add --group $setupGroupId --member-id $vmPrincipalId
if ($LASTEXITCODE -ne 0) { throw 'Setup group membership failed.' }
az role assignment create --assignee-object-id $vmPrincipalId --assignee-principal-type ServicePrincipal --role AcrPull --scope $registryId --query id -o tsv
if ($LASTEXITCODE -ne 0) { throw 'Temporary registry grant failed.' }
az role assignment create --assignee-object-id $vmPrincipalId --assignee-principal-type ServicePrincipal --role 'Key Vault Secrets Officer' --scope $vaultId --query id -o tsv
if ($LASTEXITCODE -ne 0) { throw 'Temporary vault grant failed.' }
```

Create distinct retained user-assigned identities for operator and storage maintenance in the
production group, and a dedicated mail identity for Graph. Attach operator to the VM for tenant
bootstrap; attach mail only for the bounded mail authorization test. Maintenance is needed only for
storage/restore operations. The production worker attaches mail through its template. Record each
identity's resource ID, `principalId` and `clientId` using `az identity show`.

Create each retained identity with `az identity create -g $group -n $identityName --location $location`;
attach a selected resource ID with `az vm identity assign -g $temporaryGroup -n $vmName --identities $identityId`.
Stop on a nonzero exit and inspect existing identities before retrying; the system identity must remain
present. Record every attachment so cleanup does not delete the retained production identities.

For web, worker and migration, read each resource's `identity.principalId`; obtain its application
client ID using `az ad sp show --id $principalId --query appId -o tsv`. Verify subscription/tenant and
record the pair. Assemble the documented five-entry identity file, using unique SQL names:

```json
{"version":1,"identities":[
  {"role":"workbench_web","name":"workbench_web_prod","principalId":"WEB-PRINCIPAL-UUID","clientId":"WEB-CLIENT-UUID"},
  {"role":"workbench_worker","name":"workbench_worker_prod","principalId":"WORKER-PRINCIPAL-UUID","clientId":"WORKER-CLIENT-UUID"},
  {"role":"workbench_migrator","name":"workbench_migrator_prod","principalId":"MIGRATOR-PRINCIPAL-UUID","clientId":"MIGRATOR-CLIENT-UUID"},
  {"role":"workbench_operator","name":"workbench_operator_prod","principalId":"OPERATOR-PRINCIPAL-UUID","clientId":"OPERATOR-CLIENT-UUID"},
  {"role":"workbench_storage_maintenance","name":"workbench_maintenance_prod","principalId":"MAINTENANCE-PRINCIPAL-UUID","clientId":"MAINTENANCE-CLIENT-UUID"}
]}
```

Replace all placeholders with readbacks; no secrets belong in this file. SQL uses client IDs, while
Azure/Exchange grants use principal IDs. Do not invent identities based on display names.

## Generate files and initialize SQL

In Run Command, set the nonsecret `SQL_HOST`, `OPERATOR_CLIENT_ID`, `REGISTRY`, `IMAGE` (full digest)
and `VAULT` variables from the worksheet and export them. Generate files once; the commands below
refuse an existing directory to avoid accidentally rotating an installation's proof/certificate:

```bash
set -euo pipefail
umask 077
mkdir /root/workbench-bootstrap
cd /root/workbench-bootstrap
python3 - <<'PY'
import os, secrets, base64
from pathlib import Path
for name, value in {
    'tenant-proof': base64.b64encode(secrets.token_bytes(32)).decode(),
    'protection-password': secrets.token_urlsafe(48),
    'administrator-password': 'Wb-aA9!'+secrets.token_urlsafe(48)
}.items():
    Path(name).write_text(value)
base = ('Server=tcp:'+os.environ['SQL_HOST']+',1433;Database=Workbench;'
        'Encrypt=True;TrustServerCertificate=False;'
        'Authentication=Active Directory Managed Identity;Max Pool Size=5;')
Path('setup-connection').write_text(base)
Path('migration-connection').write_text(base)
Path('operator-connection').write_text(base+'User Id='+os.environ['OPERATOR_CLIENT_ID']+';')
PY
openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 365 \
  -subj '/CN=Workbench Data Protection' -keyout protection.key -out protection.crt
openssl pkcs12 -export -inkey protection.key -in protection.crt \
  -out protection.pfx -passout file:protection-password
openssl pkcs12 -in protection.pfx -passin file:protection-password -info -noout
openssl base64 -A -in protection.pfx -out protection-pfx
az login --identity --output none
az acr login --name "$REGISTRY" --output none
docker pull "$IMAGE"
docker image inspect "$IMAGE" --format '{{.Os}}/{{.Architecture}} {{index .Config.Labels "org.opencontainers.image.revision"}}'
echo BOOTSTRAP_FILES_READY
```

The migration file selects the identity of the host where it is consumed: setup VM for the first
migration, migration job for later runs. Its filename does not impersonate the job. Keep the database
name explicit. Validate the pulled digest/source label against the worksheet. Transfer the nonsecret
identity JSON using a Run Command script that writes `/root/workbench-bootstrap/identities.json`;
do not embed secret contents in submitted scripts. Use the reviewed release's database tool:

```bash
set -euo pipefail
# IMAGE is the recorded immutable release reference, set explicitly in this invocation.
db() {
  docker run --rm --network host --user 0:0 \
    --mount type=bind,src=/root/workbench-bootstrap,dst=/bootstrap,readonly \
    --entrypoint dotnet "$IMAGE" /opt/workbench/database/Workbench.Database.dll "$@"
}
db migrate --connection-file /bootstrap/setup-connection --expected-database Workbench
db principals provision-entra --connection-file /bootstrap/setup-connection \
  --expected-database Workbench --identity-file /bootstrap/identities.json \
  --tenant-context-proof-key-file /bootstrap/tenant-proof
db bootstrap --connection-file /bootstrap/operator-connection --expected-database Workbench \
  --tenant-name "$TENANT_NAME" --admin-email "$ADMIN_EMAIL" \
  --password-file /bootstrap/administrator-password
echo INITIAL_TENANT_READY
```

Set `TENANT_NAME` and `ADMIN_EMAIL` from the worksheet in this invocation. The temporary administrative
container uses root only to read the root-protected mount; production containers remain non-root.
Host networking exposes the VM's IMDS identities to this trusted tool. Do not run unreviewed images
there. Stop on CLI failure, inspect without printing connection/password files, and do not blindly
repeat one-time tenant bootstrap. Verify the operator is not dbo and runtime users have only their
intended roles before activation.

Publish the four required vault files from the same private host, after grant propagation:

```bash
set -euo pipefail
cd /root/workbench-bootstrap
az login --identity --output none
for name in tenant-proof protection-pfx protection-password migration-connection; do
  az keyvault secret set --vault-name "$VAULT" --name "$name" --file "$name" \
    --encoding utf-8 --output none
  az keyvault secret download --vault-name "$VAULT" --name "$name" \
    --file "verify-$name" --encoding utf-8 --output none
  cmp "$name" "verify-$name"
done
echo VAULT_FILES_VERIFIED
```

Do not print `az keyvault secret show` output. On a retry remove only the exact temporary verification
files after inspection; never regenerate the original proof to fix a permission problem. SMTP users
must prepare `smtp-password` as an additional protected input; Graph users must omit it.

## Initial SQL authority and runtime grants

Use the [database principal matrix](database-principals.md) for the complete role contract.
The initial SQL commands above require a protected setup connection authenticated as the SQL Entra
administrator and a different operator connection mapped to `workbench_operator`. Both target
`Workbench`, require encryption/certificate validation and an authentication method available on
that host. Copying a job connection file onto a laptop does not impersonate its managed identity.
Keep connection files, tenant proof and administrator password in protected storage.
Provisioning requires migrated tables/roles; it creates neither schema nor administrator account.
Bootstrap is one-time. Later migration-job success cannot substitute for this initial setup.
Verify administrator login through the runtime identity without granting it SQL setup authority.

For step 4, set these nonsecret variables from recorded resource outputs. Preview and approve the
account/secret-scoped grants, then create the same deployment. Keep `grantAccess=true` in the main
installation file even though this command applies only the access module:

```powershell
az deployment group what-if -g $group --template-file infra/azure/modules/access.bicep `
    --parameters storageName=$storageName vaultName=$vaultName webPrincipalId=$webPrincipalId `
    workerPrincipalId=$workerPrincipalId migrationPrincipalId=$migrationPrincipalId deliveryProvider=$deliveryProvider
if ($LASTEXITCODE -ne 0) { throw 'Access preview failed.' }
# After approval of this exact preview:
az deployment group create -g $group -n "${prefix}-access" --template-file infra/azure/modules/access.bicep `
    --parameters storageName=$storageName vaultName=$vaultName webPrincipalId=$webPrincipalId `
    workerPrincipalId=$workerPrincipalId migrationPrincipalId=$migrationPrincipalId deliveryProvider=$deliveryProvider `
    --output none
if ($LASTEXITCODE -ne 0) { throw 'Access deployment failed.' }
```

This first-install command uses the empty retained-certificate default. During later certificate
rotation include the reviewed `previousCertificates` array through a parameter file instead.

## Independent recovery copy

Before deleting this host, preserve the proof, PFX, its password, installation metadata and initial
administrator password in encrypted recovery storage outside Azure. One portable method uses a
locally generated RSA recovery keypair: create it on the operator's machine with OpenSSL, entering
the encryption password interactively, store the encrypted private key and password in Bitwarden,
and send only the public certificate to the VM:

```bash
openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 \
  -subj '/CN=Workbench Recovery Transfer' -keyout recovery-private.pem -out recovery-public.pem
```

Write the public PEM through Run Command into the protected directory. Include a nonsecret
`installation.json` from the worksheet, then encrypt on the VM:

```bash
set -euo pipefail
cd /root/workbench-bootstrap
tar -cf recovery.tar tenant-proof protection.pfx protection-password administrator-password installation.json
openssl cms -encrypt -binary -aes-256-cbc -in recovery.tar -out recovery.cms \
  -outform DER recovery-public.pem
sha256sum recovery.tar recovery.cms
stat -c %s recovery.cms
```

Transfer only `recovery.cms`, not the plaintext tar. With Run Command output limits, use bounded
chunks: `dd if=/root/workbench-bootstrap/recovery.cms bs=1500 skip=N count=1 status=none | base64 -w0`
for consecutive N starting at zero. Decode each base64 chunk locally, concatenate in order and verify
the exact encrypted size and SHA256. These are ciphertext chunks, not private-key exports.
Decrypt locally with `openssl cms -decrypt -binary -inform DER -in recovery.cms -recip recovery-public.pem
-inkey recovery-private.pem -out recovered.tar` (one command line); enter the private-key password at
the prompt. Verify the plaintext tar SHA256 and the contents including private-key presence in the
PFX. This format relies on the independently recorded hashes for integrity; it is not an AEAD format.

Store the encrypted bundle, encrypted recovery key, public certificate, both hashes and password in
Bitwarden; download fresh copies and repeat decryption/verification in a separate directory. Do not
rely on the original machine's DPAPI key. The initial password becomes historical after account reset.
Only after this verification and all host-dependent checks pass, follow
[temporary host cleanup](azure-deployment.md#temporary-administrative-host-cleanup).
