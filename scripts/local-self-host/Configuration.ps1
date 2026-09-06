# Copyright (c) 2026 The White Stag Collection.
function Get-LocalSetupConfiguration([hashtable]$Values) {
    $allowed = @('TenantName', 'AdminEmail', 'InstallationRoot', 'SourceRef', 'TrustLocalCertificate', 'HttpPort', 'HttpsPort')
    foreach ($key in $Values.Keys) {
        if ($key -notin $allowed) { throw "Unknown configuration field: $key" }
    }
    if ([string]::IsNullOrWhiteSpace($Values.TenantName) -or $Values.TenantName.Length -gt 200 -or $Values.TenantName -match '[\r\n\x00]') { throw 'A tenant name of 1-200 characters is required.' }
    $mail = $null
    if (-not [Net.Mail.MailAddress]::TryCreate($Values.AdminEmail, [ref]$mail) -or $mail.Address -cne $Values.AdminEmail -or $Values.AdminEmail.Length -gt 256) { throw 'An administrator email address of at most 256 characters is required.' }
    $result = @{
        TenantName = $Values.TenantName; AdminEmail = $Values.AdminEmail
        InstallationRoot = (Join-Path $env:LOCALAPPDATA 'WorkbenchSelfHost')
        SourceRef = 'HEAD'; TrustLocalCertificate = $false; HttpPort = 80; HttpsPort = 443
    }
    foreach ($key in $Values.Keys) { $result[$key] = $Values[$key] }
    if ($result.TrustLocalCertificate -isnot [bool]) { throw 'TrustLocalCertificate must be a JSON boolean.' }
    foreach ($port in @('HttpPort', 'HttpsPort')) {
        if ($result[$port] -isnot [int] -and $result[$port] -isnot [long] -or $result[$port] -lt 1 -or $result[$port] -gt 65535) { throw 'Ports must be integers from 1 to 65535.' }
    }
    if ($result.HttpPort -eq $result.HttpsPort) { throw 'HTTP and HTTPS ports must differ.' }
    if ([string]::IsNullOrWhiteSpace($result.SourceRef) -or $result.SourceRef.StartsWith('-')) { throw 'SourceRef must name a Git revision.' }
    if ($result.InstallationRoot -notmatch '^[A-Za-z]:[\\/]' -or -not [IO.Path]::IsPathFullyQualified($result.InstallationRoot) -or $result.InstallationRoot -match '[,$''\r\n\x00]') { throw 'Use an absolute local installation path without quotes, dollar signs, commas, or newlines.' }
    $result.InstallationRoot = [IO.Path]::GetFullPath($result.InstallationRoot).TrimEnd('\', '/')
    return $result
}
function New-LocalConnection([string]$Role, [string]$Password) {
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Server'] = 'tcp:sql,1433'; $builder['Database'] = 'Workbench'
    $builder['User ID'] = if ($Role -eq 'setup') { 'sa' } else { "workbench_${Role}_local" }
    $builder['Password'] = $Password; $builder['Encrypt'] = $true
    $builder['TrustServerCertificate'] = $false; $builder['Persist Security Info'] = $false
    $builder['Connect Timeout'] = 15
    $builder['Max Pool Size'] = if ($Role -in @('web', 'worker')) { 20 } else { 5 }
    return $builder.ConnectionString
}
