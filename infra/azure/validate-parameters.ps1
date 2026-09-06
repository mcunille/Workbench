# Copyright (c) 2026 The White Stag Collection.
param([string] $ParametersFile)
function Test-MappedAddressOverlap([Net.IPNetwork] $Network) {
    if ($Network.BaseAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetworkV6) { return $false }
    $address = $Network.BaseAddress.GetAddressBytes()
    $mapped = [Net.IPAddress]::Parse('::ffff:192.0.2.1').GetAddressBytes()
    for ($bit = 0; $bit -lt [Math]::Min($Network.PrefixLength, 96); $bit++) {
        $mask = 1 -shl (7 - $bit % 8)
        $index = [int][Math]::Floor($bit / 8)
        if (($address[$index] -band $mask) -ne ($mapped[$index] -band $mask)) { return $false }
    }
    return $true
}
function Test-WorkbenchAzureParameters([hashtable] $Document) {
    $p = $Document.parameters
    if ($p.image.value -notmatch '^[a-z0-9.-]+(?::[0-9]+)?/[a-z0-9/._-]+@sha256:[a-f0-9]{64}$') {
        throw 'The image must contain a registry/repository and immutable lowercase SHA-256 digest.'
    }
    if (-not $p.image.value.StartsWith($p.registryServer.value + '/', [StringComparison]::Ordinal)) {
        throw 'Image and configured registry must match.'
    }
    foreach ($key in @('installationId', 'sqlAdminObjectId')) {
        $parsed = [guid]::Empty
        if (-not [guid]::TryParse($p[$key].value, [ref] $parsed) -or $parsed -eq [guid]::Empty) {
            throw "A stable, nonempty $key UUID is required."
        }
    }
    $certificateNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('protection-pfx', 'protection-password')) { [void] $certificateNames.Add($name) }
    foreach ($certificate in $p.previousCertificates.value) {
        foreach ($name in @($certificate.secretName, $certificate.passwordSecretName)) {
            if ($name -notmatch '^protection-[a-z0-9-]{1,100}$' -or -not $certificateNames.Add($name)) {
                throw 'Retained certificates require unique protection-prefixed secret names distinct from current secrets.'
            }
        }
    }
    $origin = $null
    if (-not [uri]::TryCreate($p.publicOrigin.value, [UriKind]::Absolute, [ref] $origin) -or
        $origin.Scheme -ne 'https' -or $origin.Port -ne 443 -or $origin.UserInfo -or $origin.Query -or $origin.Fragment -or
        $origin.AbsolutePath -ne '/' -or $origin.Host -ne $p.publicHost.value -or
        $p.publicHost.value.Contains('*') -or $p.publicHost.value.Contains(';')) {
        throw 'Use one canonical HTTPS origin and its explicit allowed hostname.'
    }
    if ($p.activate.value -and (-not $p.grantAccess.value -or
        (@($p.trustedProxyAddresses.value).Count + @($p.trustedProxyNetworks.value).Count) -eq 0)) {
        throw 'Activation requires pre-provisioned scoped access and observed proxy trust.'
    }
    foreach ($address in $p.trustedProxyAddresses.value) {
        $ip = $null
        if (-not [Net.IPAddress]::TryParse($address, [ref] $ip) -or
            $ip.Equals([Net.IPAddress]::Any) -or $ip.Equals([Net.IPAddress]::IPv6Any)) {
            throw 'Proxy addresses must be explicit socket peer addresses.'
        }
    }
    foreach ($network in $p.trustedProxyNetworks.value) {
        $cidr = [Net.IPNetwork]::new([Net.IPAddress]::Any, 0)
        if (-not [Net.IPNetwork]::TryParse($network, [ref] $cidr) -or
            ($cidr.BaseAddress.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and $cidr.PrefixLength -lt 24) -or
            ($cidr.BaseAddress.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6 -and $cidr.PrefixLength -lt 64) -or
            (Test-MappedAddressOverlap $cidr)) {
            throw 'Use only observed narrow ingress CIDRs (IPv4 /24 or narrower; IPv6 /64 or narrower).'
        }
    }
    if ($p.publishIngress.value) {
        if (-not $p.publicHost.value.EndsWith('.azurecontainerapps.io', [StringComparison]::OrdinalIgnoreCase) -and
            -not $p.customDomainCertificateId.value) {
            throw 'Public custom hostnames require a verified Container Apps environment certificate resource ID.'
        }
        if (-not $p.activate.value -or @($p.releaseTraffic.value).Count -eq 0) {
            throw 'Public ingress requires an active, verified revision and explicit traffic allocation.'
        }
        $total = 0
        foreach ($traffic in $p.releaseTraffic.value) {
            if (-not $traffic.revisionName -or $traffic.latestRevision -or
                $traffic.weight -lt 0 -or $traffic.weight -gt 100) {
                throw 'Traffic must reference named verified revisions and a valid weight.'
            }
            $total += $traffic.weight
        }
        if ($total -ne 100) { throw 'Public revision traffic must sum to 100.' }
    }
    if ($p.customDomainCertificateId.value -and $p.customDomainCertificateId.value -notmatch
        '^/subscriptions/[0-9a-f-]+/resourceGroups/[^/]+/providers/Microsoft\.App/managedEnvironments/[^/]+/(managedCertificates|certificates)/[^/]+$') {
        throw 'The custom domain certificate must be a Container Apps environment certificate resource ID.'
    }
}
if ($ParametersFile) {
    Test-WorkbenchAzureParameters (Get-Content -LiteralPath $ParametersFile -Raw | ConvertFrom-Json -AsHashtable)
}
