# Copyright (c) 2026 The White Stag Collection.
[CmdletBinding()]
param(
    [string]$InstallationRoot = (Join-Path $env:LOCALAPPDATA 'WorkbenchSelfHost')
)

$ErrorActionPreference = 'Stop'
$secretsRoot = Join-Path $InstallationRoot 'secrets'
if (-not (Test-Path -LiteralPath $secretsRoot -PathType Container)) {
    throw 'Create and protect the installation secrets folder first.'
}
$tlsRoot = Join-Path $secretsRoot 'sql-tls'
$trustRoot = Join-Path $InstallationRoot 'trust'
$rootCertificatePath = Join-Path $trustRoot 'sql-ca.crt'
$rootArchivePath = Join-Path $secretsRoot 'sql-ca.pfx'
$rootPasswordPath = Join-Path $secretsRoot 'sql-ca-password'
$serverCertificatePath = Join-Path $tlsRoot 'server.pem'
$serverKeyPath = Join-Path $tlsRoot 'server.key'
foreach ($path in @($rootCertificatePath, $rootArchivePath, $rootPasswordPath, $serverCertificatePath, $serverKeyPath)) {
    if (Test-Path -LiteralPath $path) { throw 'Certificate files already exist. Preserve them; inspect before retrying or rotating.' }
}

$rootKey = [Security.Cryptography.RSA]::Create(3072)
$serverKey = [Security.Cryptography.RSA]::Create(3072)
$rootCertificate = $null
$serverCertificate = $null
$chain = $null
try {
    $hash = [Security.Cryptography.HashAlgorithmName]::SHA256
    $padding = [Security.Cryptography.RSASignaturePadding]::Pkcs1
    $rootRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=Workbench Local SQL CA', $rootKey, $hash, $padding)
    $rootRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $true, 0, $true))
    $rootRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign, $true))
    $rootRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($rootRequest.PublicKey, $false))
    $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $rootCertificate = $rootRequest.CreateSelfSigned($notBefore, [DateTimeOffset]::UtcNow.AddYears(10))

    $serverRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=sql', $serverKey, $hash, $padding)
    $serverRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $serverRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))
    $usages = [Security.Cryptography.OidCollection]::new()
    [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $serverRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $true))
    $names = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $names.AddDnsName('sql')
    $serverRequest.CertificateExtensions.Add($names.Build())
    $serial = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $serial[0] = ($serial[0] -band 0x7f) -bor 1
    $serverCertificate = $serverRequest.Create($rootCertificate, $notBefore, [DateTimeOffset]::UtcNow.AddDays(365), $serial)

    # Verify the intended chain and server-authentication usage without installing OS trust.
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.TrustMode = [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
    [void]$chain.ChainPolicy.CustomTrustStore.Add($rootCertificate)
    $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    [void]$chain.ChainPolicy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    if (-not $chain.Build($serverCertificate)) { throw 'Generated SQL certificate did not pass chain validation.' }

    New-Item -ItemType Directory -Path $tlsRoot, $trustRoot -Force | Out-Null
    $archivePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [IO.File]::WriteAllText($rootPasswordPath, $archivePassword)
    [IO.File]::WriteAllBytes($rootArchivePath, $rootCertificate.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $archivePassword))
    [IO.File]::WriteAllText($rootCertificatePath, $rootCertificate.ExportCertificatePem())
    [IO.File]::WriteAllText($serverCertificatePath, $serverCertificate.ExportCertificatePem())
    [IO.File]::WriteAllText($serverKeyPath, $serverKey.ExportPkcs8PrivateKeyPem())
    Write-Host 'SQL certificate generated and chain verified for hostname sql.'
    Write-Host ('SQL certificate expires (UTC): ' + $serverCertificate.NotAfter.ToUniversalTime().ToString('u'))
    Write-Host 'No services started and no operating-system trust settings changed.'
}
finally {
    if ($chain) { $chain.Dispose() }
    if ($serverCertificate) { $serverCertificate.Dispose() }
    if ($rootCertificate) { $rootCertificate.Dispose() }
    $serverKey.Dispose()
    $rootKey.Dispose()
    Remove-Variable archivePassword -ErrorAction SilentlyContinue
}
