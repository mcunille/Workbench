# Restricted-public HTTPS bootstrap validation

Use this path when a private diagnostic route cannot preserve the canonical Host and validate TLS.
It is the path used in the accepted installation after internal routes returned 404/timeouts.
This is an explicitly authorized, operator-IP-restricted public endpoint, not a private-ingress test.
SQL, Blob and Key Vault remain private throughout. Do not disable TLS/host validation or open all
clients just to complete bootstrap. If a working private route is available, it may be used instead.

## Obtain the initial certificate

Record the canonical origin/hostname and operator's current public IPv4 in the installation worksheet.
Use a reviewed digest-pinned Certbot image and a protected local directory outside Git. On the
operator's Docker host, set `$certbotImage`, `$certificateDirectory`, `$publicHost` and `$contactEmail`:

```powershell
docker run --rm -it --mount "type=bind,src=$certificateDirectory,dst=/etc/letsencrypt" `
    $certbotImage certonly --manual --preferred-challenges dns --agree-tos `
    --email $contactEmail --cert-name workbench-bootstrap -d $publicHost
```

Approve certificate issuance/terms before running. Publish the requested `_acme-challenge` TXT record
in the authoritative DNS provider; independently resolve it before continuing Certbot. Keep the
private key directory protected. Manual DNS issuance does not automatically renew: replace the
working bootstrap binding with an ACA managed certificate after authorized DNS/ingress validation.

Use OpenSSL on the host containing these files to export PKCS#12. Generate a random password into
a protected file (never into command arguments), then run:

```bash
umask 077
openssl rand -base64 48 > https-password
openssl pkcs12 -export -inkey live/workbench-bootstrap/privkey.pem \
  -in live/workbench-bootstrap/fullchain.pem -out https.pfx -passout file:https-password
openssl pkcs12 -in https.pfx -passin file:https-password -info -noout
openssl x509 -in live/workbench-bootstrap/cert.pem -noout -subject -dates -fingerprint -sha256
```

Validate hostname/chain/expiry and key presence. Upload via a protected REST body file rather than
putting the PFX password in CLI arguments. Set `$environmentId` from foundation outputs, `$pfxPath`,
`$passwordPath`, and a new `$certificateBodyFile` outside Git. Do not display that body:

```powershell
$body = @{ location = $location; properties = @{
    value = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath))
    password = [IO.File]::ReadAllText($passwordPath).TrimEnd("`r", "`n")
} }
$body | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $certificateBodyFile
$certificateId = "$environmentId/certificates/workbench-bootstrap"
az rest --method put --url "https://management.azure.com${certificateId}?api-version=2025-01-01" --body "@$certificateBodyFile" --output none
if ($LASTEXITCODE -ne 0) { throw 'Certificate upload failed.' }
az rest --method get --url "https://management.azure.com${certificateId}?api-version=2025-01-01" --query 'properties.{state:provisioningState,subject:subjectName,expires:expirationDate}'
```

Delete the temporary upload body after verifying success; retain the protected source until managed
certificate replacement is verified. Store only `$certificateId`, not its bytes/password, in parameters.

## Prepare DNS, stage and restrict traffic

Read `properties.customDomainVerificationId` from `az containerapp show`. In DNS, create the ownership
TXT record `asuid.<subdomain>` with that exact value. Keep it after validation. Prepare the CNAME for
the canonical hostname to `<app>.<environment-default-domain>` (external form, without `.internal`).
Confirm the record's authoritative and public answers before browser verification. Publish no wildcard.

After SQL provisioning, scoped secret grants and the manual migration job succeed, set `activate=true`,
`publishIngress=false`, `workerEnabled=false`, `enablePublicRecovery=false` and the uploaded certificate
ID. Use the [parameter helper](azure-release.md#stage-without-switching-traffic) and scoped workloads
deployment; do not redeploy foundation while the temporary administrative subnet exists. Require the
candidate's startup/readiness probes to pass; read its exact revision name.

For the separately approved restricted-public step, set `publishIngress=true`, keep the same image,
and pin `releaseTraffic` to that revision at weight 100. Set `ingressPolicy` to
`{"mode":"Restricted","allowCidrs":["YOUR-PUBLIC-IP/32"]}` with the actual canonical IPv4 CIDR.
Run validation/parameter assembly again, inspect what-if and apply only after approval of these
changes. A template default must never silently replace this allowlist with Public mode.

Read back `external=true`, `allowInsecure=false`, the exact /32 rule, SNI binding and 100% pinned traffic.
Through the canonical hostname require home/readiness 200, anonymous identity 401, security headers,
actual sign-in and worker mail delivery. A routing 404 is not an application readiness success.
Resolve stale local DNS caches before changing correct public records; do not bypass certificate checks.

Then follow [managed certificate transition](azure-deployment.md#trust-tls-and-readiness-acceptance)
and [public opening](azure-release.md#open-public-access-separately). If issuance requires additional
issuer reachability, review the current Microsoft requirements and approve exact temporary rules
separately; do not hard-code an old issuer IP list. Keep the working certificate until replacement is
bound and verified. Remove the obsolete ACME challenge only after issuance; retain domain ownership
TXT. Public recovery and scheduled worker activation have their own successful delivery gates.
