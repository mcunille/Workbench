# Copyright (c) 2026 The White Stag Collection.
$ErrorActionPreference = 'Stop'
function Assert-Rejected($Document, $Reason) {
    try { Test-WorkbenchAzureParameters $Document; throw 'accepted-invalid-configuration' }
    catch { if ($_.Exception.Message -eq 'accepted-invalid-configuration') { throw $Reason } }
}
. "$PSScriptRoot/validate-parameters.ps1"
# GIVEN a complete inactive installation with immutable image and canonical HTTPS origin
$document = Get-Content "$PSScriptRoot/main.parameters.example.json" -Raw | ConvertFrom-Json -AsHashtable
$document.parameters.image.value = 'example.azurecr.io/workbench@sha256:' + ('a' * 64)
$document.parameters.installationId.value = 'ca434d31-5c6d-44c2-a899-0486d9facd45'
$document.parameters.sqlAdminObjectId.value = 'bf125b43-8eac-4f23-baca-2a264f11f7df'
# GIVEN an omitted access policy WHEN checked THEN publication cannot silently become public.
$document.parameters.Remove('ingressPolicy')
Assert-Rejected $document 'Missing ingress policy was accepted.'
# GIVEN Restricted access without clients WHEN checked THEN an empty ACA allow list is rejected.
$document.parameters.ingressPolicy = @{ value = @{ mode = 'Restricted'; allowCidrs = @() } }
Assert-Rejected $document 'Empty Restricted ingress was accepted.'
foreach ($invalidCidr in @('0.0.0.0/0', '::/0', 'not-a-cidr', '192.0.2.1', '192.0.2.1/24', '::ffff:192.0.2.1/128')) {
    $document.parameters.ingressPolicy.value.allowCidrs = @($invalidCidr)
    Assert-Rejected $document 'Invalid or universal ingress CIDR was accepted.'
}
# GIVEN explicit client networks WHEN checked THEN private bootstrap supports Restricted policy.
$document.parameters.ingressPolicy.value.allowCidrs = @('192.0.2.0/24', '198.51.100.7/32')
Test-WorkbenchAzureParameters $document
# GIVEN Public with lingering rules WHEN checked THEN conflicting intent is rejected.
$document.parameters.ingressPolicy.value.mode = 'Public'
Assert-Rejected $document 'Public policy with restrictions was accepted.'
$document.parameters.ingressPolicy.value.allowCidrs = @()
Test-WorkbenchAzureParameters $document
$document.parameters.ingressPolicy.value.mode = 'Unknown'
Assert-Rejected $document 'Unknown ingress policy mode was accepted.'
$document.parameters.ingressPolicy.value = @{ mode = 'Restricted'; allowCidrs = @('198.51.100.7/32') }
# WHEN configuration is checked THEN valid inactive bootstrap succeeds
Test-WorkbenchAzureParameters $document
# GIVEN an HTTPS origin on a port ACA does not expose WHEN checked THEN links cannot target it
$document.parameters.publicOrigin.value = 'https://workbench.example.com:8443'
Assert-Rejected $document 'An unsupported ingress port was accepted.'
$document.parameters.publicOrigin.value = 'https://workbench.example.com'
# GIVEN a certificate mapping pointing at the migration connection WHEN checked THEN it is rejected
$document.parameters.previousCertificates.value = @(@{ secretName = 'migration-connection'; passwordSecretName = 'protection-old-password' })
Assert-Rejected $document 'A retained certificate mapped a migration secret into runtime access.'
$document.parameters.previousCertificates.value = @(@{ secretName = 'protection-2026-pfx'; passwordSecretName = 'protection-2026-password' })
Test-WorkbenchAzureParameters $document
$document.parameters.previousCertificates.value = @()
# GIVEN a mutable image WHEN checked THEN release validation fails
$document.parameters.image.value = 'example.azurecr.io/workbench:latest'
Assert-Rejected $document 'Mutable image was accepted.'
$document.parameters.image.value = 'example.azurecr.io/workbench@sha256:' + ('a' * 64)
# GIVEN an origin containing a token WHEN checked THEN configuration fails
$document.parameters.publicOrigin.value = 'https://workbench.example.com/?token=sensitive'
Assert-Rejected $document 'An origin query was accepted.'
$document.parameters.publicOrigin.value = 'https://workbench.example.com'
# GIVEN activation without scoped grants WHEN checked THEN configuration fails
$document.parameters.activate.value = $true
Assert-Rejected $document 'Activation without grants was accepted.'
$document.parameters.grantAccess.value = $true
$document.parameters.trustedProxyNetworks.value = @('10.42.0.0/24')
Test-WorkbenchAzureParameters $document
$document.parameters.trustedProxyNetworks.value = @('fd00:42::/64')
Test-WorkbenchAzureParameters $document
$document.parameters.trustedProxyNetworks.value = @()
# GIVEN activation without an observed proxy WHEN checked THEN configuration fails
Assert-Rejected $document 'Empty proxy trust was accepted.'
# GIVEN an explicit Azure environment metadata boundary WHEN activated THEN address discovery is unnecessary.
$document.parameters.proxyTrustMode = @{ value = 'AzureContainerApps' }
Test-WorkbenchAzureParameters $document
# GIVEN the same boundary without scoped grants WHEN activated THEN data access prerequisites still apply.
$document.parameters.grantAccess.value = $false
Assert-Rejected $document 'Azure metadata trust bypassed scoped access prerequisites.'
$document.parameters.grantAccess.value = $true
$document.parameters.trustedProxyAddresses.value = @('10.42.0.2')
Assert-Rejected $document 'Mixed Azure and explicit proxy trust was accepted.'
$document.parameters.trustedProxyAddresses.value = @()
$document.parameters.proxyTrustMode.value = 'Azure'
Assert-Rejected $document 'Unknown proxy trust mode was accepted.'
$document.parameters.proxyTrustMode.value = 'KnownProxies'
$document.parameters.trustedProxyNetworks.value = @('0.0.0.0/0')
Assert-Rejected $document 'Universal proxy trust was accepted.'
$document.parameters.trustedProxyNetworks.value = @('10.0.0.0/8')
Assert-Rejected $document 'All-private network trust was accepted.'
$document.parameters.trustedProxyNetworks.value = @('::ffff:10.0.0.0/104')
Assert-Rejected $document 'Mapped all-private IPv4 trust was accepted.'
$document.parameters.trustedProxyNetworks.value = @('::/64')
Assert-Rejected $document 'An IPv6 network containing all mapped IPv4 addresses was accepted.'
$document.parameters.trustedProxyNetworks.value = @()
$document.parameters.trustedProxyAddresses.value = @('10.42.0.7')
Test-WorkbenchAzureParameters $document
# GIVEN public ingress without pinned traffic WHEN checked THEN configuration fails
$document.parameters.publishIngress.value = $true
Assert-Rejected $document 'Unpinned public ingress was accepted.'
$document.parameters.releaseTraffic.value = @(@{ revisionName = 'wb-example-web--verified'; weight = 100 })
Assert-Rejected $document 'A public custom hostname was accepted without a certificate binding.'
$document.parameters.customDomainCertificateId.value = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/example/providers/Microsoft.App/managedEnvironments/wb-example-environment/managedCertificates/verified-domain'
Test-WorkbenchAzureParameters $document
Write-Host 'Azure parameter contract checks passed.'
# GIVEN Graph delivery without a mailbox WHEN checked THEN incomplete configuration is rejected
$document.parameters.deliveryProvider = @{ value = 'Graph' }
$document.parameters.graphMailboxId = @{ value = '' }
$document.parameters.graphManagedIdentityClientId = @{ value = '' }
$document.parameters.mailIdentityId = @{ value = '' }
Assert-Rejected $document 'Graph without an explicit mailbox and identity was accepted.'
$document.parameters.graphMailboxId.value = '090663bf-f1ad-4192-8e96-e4fa5bf98414'
$document.parameters.graphManagedIdentityClientId.value = 'c7a39d21-9559-413f-a615-fbd86f1d39da'
$document.parameters.mailIdentityId.value = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/example/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mail'
# WHEN Graph has a dedicated identity THEN SMTP fields may be empty
$document.parameters.smtpHost.value = ''
$document.parameters.smtpUsername.value = ''
$document.parameters.smtpSender.value = ''
Test-WorkbenchAzureParameters $document
# GIVEN the pull identity reused for mail WHEN checked THEN identity separation is enforced
$document.parameters.mailIdentityId.value = $document.parameters.registryPullIdentityId.value
Assert-Rejected $document 'Registry pull identity was accepted as mail identity.'
