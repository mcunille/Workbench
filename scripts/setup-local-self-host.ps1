# Copyright (c) 2026 The White Stag Collection.
#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$ConfigurationFile,
    [string]$TenantName,
    [string]$AdminEmail,
    [string]$InstallationRoot,
    [string]$SourceRef,
    [switch]$TrustLocalCertificate,
    [int]$HttpPort,
    [int]$HttpsPort
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows) { throw 'This installer requires Windows, PowerShell 7, and Docker Desktop with Linux containers.' }
. "$PSScriptRoot/local-self-host/Configuration.ps1"
$values = if ($ConfigurationFile) { Get-Content -LiteralPath $ConfigurationFile -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
foreach ($key in @('TenantName','AdminEmail','InstallationRoot','SourceRef','HttpPort','HttpsPort')) {
    if ($PSBoundParameters.ContainsKey($key)) { $values[$key] = $PSBoundParameters[$key] }
}
if ($PSBoundParameters.ContainsKey('TrustLocalCertificate')) { $values.TrustLocalCertificate = [bool]$TrustLocalCertificate }
$settings = Get-LocalSetupConfiguration $values
$root = $settings.InstallationRoot
if (Test-Path -LiteralPath $root) { throw 'Installation root already exists. Existing installations are never overwritten or resumed automatically.' }
$docker = Get-Command docker -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $docker) {
    foreach ($candidate in @("$env:LOCALAPPDATA/Programs/DockerDesktop/resources/bin/docker.exe", 'C:/Program Files/Docker/Docker/resources/bin/docker.exe')) {
        if (Test-Path -LiteralPath $candidate) { $docker = Get-Command $candidate; break }
    }
}
if (-not $docker) { throw 'Docker Desktop CLI is required.' }
function Invoke-SetupDocker([string[]]$Arguments) {
    $output = & $docker.Source @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Docker operation failed. Installation state is preserved; inspect the current stage before retrying.' }
    return $output
}
if ((Invoke-SetupDocker @('info','--format','{{.OSType}}')) -ne 'linux') { throw 'Select Docker Linux containers.' }
# Reject caller environment overrides before they can alter the generated deployment.
if (Get-ChildItem Env: | Where-Object Name -Match '^(COMPOSE_|WORKBENCH_|ConnectionStrings__|Storage__|DataProtection__)') { throw 'Run setup in a clean shell without deployment overrides.' }
foreach ($port in @($settings.HttpPort, $settings.HttpsPort)) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
    try { $listener.Start() } finally { $listener.Stop() }
}
$repository = Split-Path -Parent $PSScriptRoot
$revision = & git -C $repository rev-parse --verify "$($settings.SourceRef)^{commit}"
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40}$') { throw 'SourceRef must resolve to a committed revision.' }
$project = 'workbench-local-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant()))).Substring(0,10).ToLowerInvariant()
$names = Invoke-SetupDocker @('container','ls','-a','--filter',"label=com.docker.compose.project=$project",'--format','{{.ID}}')
if ($names) { throw 'Containers for this installation already exist.' }
$existingVolumes = @(Invoke-SetupDocker @('volume','ls','--format','{{.Name}}'))
$existingNetworks = @(Invoke-SetupDocker @('network','ls','--format','{{.Name}}'))
foreach ($suffix in @('sql-data','sql-tls','blobs','proxy-data','proxy-config')) {
    if ("${project}_$suffix" -in $existingVolumes) { throw 'Retained installation volumes already exist; refusing to adopt them.' }
}
foreach ($suffix in @('dependencies','ingress')) {
    if ("${project}_$suffix" -in $existingNetworks) { throw 'Retained installation networks already exist; refusing to adopt them.' }
}
$networkIds = @(Invoke-SetupDocker @('network','ls','--format','{{.ID}}'))
$used = @()
foreach ($id in $networkIds) {
    $network = (Invoke-SetupDocker @('network','inspect',"$id") | Out-String | ConvertFrom-Json)[0]
    foreach ($entry in $network.IPAM.Config) { if ($entry.Subnet) { $used += [Net.IPNetwork]::Parse($entry.Subnet) } }
}
$subnet = $null
foreach ($index in 74..254) {
    $candidate = [Net.IPNetwork]::Parse("172.30.$index.0/24")
    if (-not ($used | Where-Object { $_.Contains($candidate.BaseAddress) -or $candidate.Contains($_.BaseAddress) })) { $subnet = "172.30.$index"; break }
}
if (-not $subnet) { throw 'No unused installation ingress subnet was found.' }
$sqlImage = 'mcr.microsoft.com/mssql/server@sha256:7c29dfbac885ad7519e219c7fe4aee0e67283e21a10e9c252d13b0fbde1866f8'
$proxyImage = 'caddy@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d'
$stage = 'prepare'
$composeFile = Join-Path $root 'compose.json'
$compose = @('compose','--project-name',$project,'--file',$composeFile)
function Save-State([string]$Status) {
    @{ Status=$Status; Stage=$stage; SourceRevision=$revision; Project=$project; UpdatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); PublicOrigin=$origin } |
        ConvertTo-Json | Set-Content -LiteralPath "$root/installation.json" -Encoding utf8
}
function New-SetupMount([string]$Source, [string]$Target, [bool]$ReadOnly = $true, [string]$Type = 'bind') {
    $mount = @{type=$Type;source=$Source;target=$Target;read_only=$ReadOnly}
    if ($Type -eq 'bind') { $mount.bind = @{create_host_path=$false} }
    return $mount
}
function Write-Secret([string]$Name, [string]$Value) { [IO.File]::WriteAllText("$root/secrets/$Name", $Value) }
function Run-Database([string]$Role, [string[]]$Command, [string[]]$AdditionalSecrets = @()) {
    $argsList = @('run','--rm','--pull','never','--network',"${project}_dependencies",'--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true',
        '--tmpfs','/tmp:rw,noexec,nosuid,size=64m,uid=1654,gid=1654','--env','SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt',
        '--mount',"type=bind,source=$root/trust/ca-certificates.crt,target=/etc/ssl/certs/ca-certificates.crt,readonly",
        '--mount',"type=bind,source=$root/secrets/$Role-connection,target=/run/secrets/connection,readonly")
    foreach ($secret in $AdditionalSecrets) { $argsList += @('--mount',"type=bind,source=$root/secrets/$secret,target=/run/secrets/$secret,readonly") }
    $argsList += @('--entrypoint','dotnet',$appImage,'/opt/workbench/database/Workbench.Database.dll') + $Command + @('--connection-file','/run/secrets/connection','--expected-database','Workbench')
    Invoke-SetupDocker $argsList | Out-Null
}
function Run-Sql([string]$Sql) {
    $command = 'export SQLCMDPASSWORD="$(cat /run/secrets/sql-bootstrap-password)"; exec /opt/mssql-tools18/bin/sqlcmd -S tcp:sql,1433 -U sa -d master -N -b -l 5 -t 60'
    $Sql | & $docker.Source @compose exec -T sql /bin/bash -ec $command *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Validated SQL operation failed.' }
}
$origin = if ($settings.HttpsPort -eq 443) { 'https://localhost' } else { "https://localhost:$($settings.HttpsPort)" }
New-Item -ItemType Directory -Path $root | Out-Null
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls $root /inheritance:r /grant:r "*${sid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' *> $null
if ($LASTEXITCODE -ne 0) { throw 'Installation ACL could not be secured. No secrets generated.' }
try {
    Save-State 'Installing'
    New-Item -ItemType Directory -Path "$root/secrets", "$root/trust", "$root/source" | Out-Null
    # Verify Docker sees host bind files before generating any credentials or starting SQL.
    $stage = 'host-file-sharing'
    Invoke-SetupDocker @('pull',$proxyImage) | Out-Null
    [IO.File]::WriteAllText("$root/mount-probe.txt", 'workbench-mount-probe')
    $probe = Invoke-SetupDocker @('run','--rm','--network','none','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true','--mount',"type=bind,source=$root/mount-probe.txt,target=/probe.txt,readonly",'--entrypoint','/bin/sh',$proxyImage,'-ec','test -f /probe.txt && cat /probe.txt')
    if (($probe | Out-String).Trim() -cne 'workbench-mount-probe') { throw 'Docker Desktop cannot read files from the selected installation root. Check host file sharing.' }
    $stage = 'build'
    Write-Host 'Building the committed release and obtaining pinned dependencies...'
    & git -C $repository archive --format=tar "--output=$root/source.tar" $revision
    if ($LASTEXITCODE -ne 0) { throw 'Git archive failed.' }
    & tar -xf "$root/source.tar" -C "$root/source"
    if ($LASTEXITCODE -ne 0) { throw 'Source archive extraction failed.' }
    Invoke-SetupDocker @('build','--pull','--label',"org.opencontainers.image.revision=$revision",'--iidfile',"$root/image-id", "$root/source") | Out-Null
    $appImage = (Get-Content "$root/image-id" -Raw).Trim()
    if ($appImage -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Build did not produce an immutable image ID.' }
    Invoke-SetupDocker @('pull',$sqlImage) | Out-Null; Invoke-SetupDocker @('pull',$proxyImage) | Out-Null
    $stage = 'secrets'
    foreach ($name in @('sql-bootstrap','web','worker','migrator','operator','maintenance','admin','certificate')) {
        Write-Secret "$name-password" ('Wb-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) + '-aA9!')
    }
    Write-Secret 'tenant-proof' ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    Write-Secret 'smtp-password' ''
    foreach ($role in @('setup','web','worker','migrator','operator','maintenance')) {
        $passwordName = if ($role -eq 'setup') { 'sql-bootstrap' } else { $role }
        Write-Secret "$role-connection" (New-LocalConnection $role ([IO.File]::ReadAllText("$root/secrets/$passwordName-password")))
    }
    & "$PSScriptRoot/local-self-host/New-SqlCertificates.ps1" -InstallationRoot $root
    $key = [Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Workbench Local Data Protection',$key,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment,$true))
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddYears(1))
        try { [IO.File]::WriteAllBytes("$root/secrets/data-protection.pfx",$certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,[IO.File]::ReadAllText("$root/secrets/certificate-password"))) } finally { $certificate.Dispose() }
    } finally { $key.Dispose() }
    $helper = "$project-trust-export"
    Invoke-SetupDocker @('create','--name',$helper,$appImage) | Out-Null
    try { Invoke-SetupDocker @('cp',"${helper}:/etc/ssl/certs/ca-certificates.crt","$root/trust/public-ca-bundle.crt") | Out-Null } finally { Invoke-SetupDocker @('rm',$helper) | Out-Null }
    [IO.File]::WriteAllText("$root/trust/ca-certificates.crt",[IO.File]::ReadAllText("$root/trust/public-ca-bundle.crt") + "`n" + [IO.File]::ReadAllText("$root/trust/sql-ca.crt") + "`n")
    $tlsVolume = "${project}_sql-tls"
    if (Invoke-SetupDocker @('volume','ls','--filter',"name=^$tlsVolume`$",'--format','{{.Name}}')) { throw 'SQL TLS volume already exists.' }
    Invoke-SetupDocker @('volume','create',$tlsVolume) | Out-Null
    $installTls = 'set -eu; cp /source/server.pem /tls/server.pem; cp /source/server.key /tls/server.key; chmod 600 /tls/server.pem /tls/server.key; chown 10001:10001 /tls/server.pem /tls/server.key; chown 10001:10001 /tls; chmod 700 /tls'
    Invoke-SetupDocker @('run','--rm','--network','none','--user','0:0','--read-only','--cap-drop','ALL','--cap-add','CHOWN','--cap-add','FOWNER','--security-opt','no-new-privileges:true','--mount',"type=bind,source=$root/secrets/sql-tls,target=/source,readonly",'--mount',"type=volume,source=$tlsVolume,target=/tls",'--entrypoint','/bin/bash',$sqlImage,'-ec',$installTls) | Out-Null
    $stage = 'compose'
    $installationId = [Guid]::NewGuid().ToString()
    $environment = @{
        ASPNETCORE_ENVIRONMENT='Production'; PublicOrigin=$origin; Storage__Provider='FileSystem'; Storage__Root='/var/lib/workbench/blobs'
        Storage__DurableVolume='true'; Storage__InstallationId=$installationId; Deployment__Replicas='1'
        WORKBENCH_TENANT_CONTEXT_PROOF_KEY_FILE='/run/secrets/tenant-proof'; WORKBENCH_DATA_PROTECTION_CERTIFICATE_PATH='/run/secrets/data-protection.pfx'
        DataProtection__CertificatePasswordFile='/run/secrets/certificate-password'; Identity__DeliveryProvider='Disabled'
        Identity__PublicRecoveryEnabled='false'; Identity__PublicInvitationEnabled='false'; SSL_CERT_FILE='/etc/ssl/certs/ca-certificates.crt'
    }
    $services = @{}
    foreach ($role in @('app','worker')) {
        $envValues = $environment.Clone()
        $credential = if ($role -eq 'app') { 'web' } else { 'worker' }
        $envValues["ConnectionStrings__$(if ($role -eq 'app') {'Workbench'} else {'Worker'})File"] = "/run/secrets/$credential-connection"
        $mounts = @((New-SetupMount 'blobs' '/var/lib/workbench/blobs' $false 'volume'), (New-SetupMount "$root/trust/ca-certificates.crt" '/etc/ssl/certs/ca-certificates.crt'))
        foreach ($secret in @("$credential-connection",'tenant-proof','data-protection.pfx','certificate-password')) { $mounts += New-SetupMount "$root/secrets/$secret" "/run/secrets/$secret" }
        $services[$role] = @{image=$appImage;pull_policy='never';user='1654:1654';init=$true;read_only=$true;restart='unless-stopped';stop_grace_period='90s';cap_drop=@('ALL');security_opt=@('no-new-privileges:true');tmpfs=@('/tmp:rw,noexec,nosuid,size=64m,uid=1654,gid=1654');environment=$envValues;volumes=$mounts;networks=@('dependencies')}
    }
    $services.app.networks = @('dependencies','ingress')
    $services.app.environment.AllowedHosts = 'localhost'; $services.app.environment.WORKBENCH_HEALTH_HOST = 'localhost'; $services.app.environment.WORKBENCH_KNOWN_PROXY = "$subnet.2"
    $services.app.healthcheck = @{test=@('CMD','dotnet','Workbench.Server.dll','--health-check');interval='10s';timeout='6s';start_period='120s';retries=3}
    $services.worker.command = @('--worker'); $services.worker.healthcheck = @{disable=$true}
    $services.sql = @{image=$sqlImage;hostname='sql';restart='unless-stopped';cap_drop=@('ALL');cap_add=@('NET_BIND_SERVICE');security_opt=@('no-new-privileges:true');networks=@('dependencies');environment=@{ACCEPT_EULA='Y';MSSQL_PID='Express';SSL_CERT_FILE='/etc/ssl/certs/ca-certificates.crt'};entrypoint=@('/bin/bash','-ec');command=@('export MSSQL_SA_PASSWORD="$$(cat /run/secrets/sql-bootstrap-password)"; exec /opt/mssql/bin/sqlservr');volumes=@((New-SetupMount 'sql-data' '/var/opt/mssql' $false 'volume'),(New-SetupMount 'sql-tls' '/var/opt/mssql/tls' $true 'volume'),(New-SetupMount "$root/source/infra/compose/mssql.conf" '/var/opt/mssql/mssql.conf'),(New-SetupMount "$root/secrets/sql-bootstrap-password" '/run/secrets/sql-bootstrap-password'),(New-SetupMount "$root/trust/ca-certificates.crt" '/etc/ssl/certs/ca-certificates.crt'))}
    @"
{
    admin off
    skip_install_trust
    auto_https disable_redirects
}
http://localhost {
    redir $origin{uri} 308
}
https://localhost {
    tls internal
    reverse_proxy app:8080 {
        header_up X-Forwarded-For {remote_host}
        header_up X-Forwarded-Proto https
        header_up -X-Forwarded-Host
    }
}
:443 {
    respond "Unrecognized host" 421
}
"@ | Set-Content "$root/Caddyfile" -Encoding utf8
    $services.proxy = @{image=$proxyImage;read_only=$true;restart='unless-stopped';cap_drop=@('ALL');cap_add=@('NET_BIND_SERVICE');security_opt=@('no-new-privileges:true');ports=@("127.0.0.1:$($settings.HttpPort):80","127.0.0.1:$($settings.HttpsPort):443");networks=@{ingress=@{ipv4_address="$subnet.2"}};volumes=@((New-SetupMount "$root/Caddyfile" '/etc/caddy/Caddyfile'),(New-SetupMount 'proxy-data' '/data' $false 'volume'),(New-SetupMount 'proxy-config' '/config' $false 'volume'))}
    @{name=$project;services=$services;networks=@{dependencies=@{};ingress=@{ipam=@{config=@(@{subnet="$subnet.0/24";ip_range="$subnet.128/25"})}}};volumes=@{blobs=@{};'sql-data'=@{};'sql-tls'=@{external=$true;name=$tlsVolume};'proxy-data'=@{};'proxy-config'=@{}}} | ConvertTo-Json -Depth 15 | Set-Content $composeFile -Encoding utf8
    Invoke-SetupDocker ($compose + @('config','--quiet')) | Out-Null
    $stage = 'sql'
    Write-Host 'Starting SQL and provisioning the database with validated TLS...'
    Invoke-SetupDocker ($compose + @('up','-d','--no-deps','sql')) | Out-Null
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    do {
        try { Run-Sql "IF NOT EXISTS (SELECT 1 FROM sys.dm_exec_connections WHERE session_id=@@SPID AND encrypt_option='TRUE') THROW 51000, 'Encryption required.', 1;"; $sqlReady=$true } catch { $sqlReady=$false; Start-Sleep -Seconds 2 }
    } while (-not $sqlReady -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $sqlReady) { throw 'SQL TLS readiness timed out.' }
    Run-Sql "IF DB_ID(N'Workbench') IS NOT NULL THROW 51000, 'Database exists.', 1; EXEC sp_configure 'contained database authentication', 1; RECONFIGURE; CREATE DATABASE [Workbench]; ALTER DATABASE [Workbench] SET CONTAINMENT=PARTIAL;"
    Run-Database 'setup' @('migrate')
    Run-Database 'setup' @('principals','provision','--web-user','workbench_web_local','--web-password-file','/run/secrets/web-password','--operator-user','workbench_operator_local','--operator-password-file','/run/secrets/operator-password','--migrator-user','workbench_migrator_local','--migrator-password-file','/run/secrets/migrator-password','--tenant-context-proof-key-file','/run/secrets/tenant-proof') @('web-password','operator-password','migrator-password','tenant-proof')
    & "$PSScriptRoot/local-self-host/Provision-WorkerRoles.ps1" -InstallationRoot $root -Network "${project}_dependencies"
    Run-Database 'operator' @('bootstrap','--tenant-name',$settings.TenantName,'--admin-email',$settings.AdminEmail,'--password-file','/run/secrets/admin-password') @('admin-password')
    $stage = 'workloads'
    Invoke-SetupDocker ($compose + @('up','-d','--no-deps','app')) | Out-Null
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do {
        & $docker.Source @compose exec -T app dotnet Workbench.Server.dll --health-check *> $null
        $ready = $LASTEXITCODE -eq 0
        if (-not $ready) { Start-Sleep -Seconds 2 }
    } while (-not $ready -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'Application readiness failed.' }
    Invoke-SetupDocker ($compose + @('run','--rm','--no-deps','worker','--worker','--once')) | Out-Null
    Invoke-SetupDocker ($compose + @('up','-d','--no-deps','worker','proxy')) | Out-Null
    $proxy = (Invoke-SetupDocker ($compose + @('ps','-q','proxy')) | Out-String).Trim()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        try { Invoke-SetupDocker @('cp',"${proxy}:/data/caddy/pki/authorities/local/root.crt","$root/trust/caddy-local-root.crt") | Out-Null; $exported=$true } catch { $exported=$false; Start-Sleep -Seconds 1 }
    } while (-not $exported -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $exported) { throw 'Caddy root export failed.' }
    $stage = 'https'
    if ($settings.TrustLocalCertificate) {
        Import-Certificate -FilePath "$root/trust/caddy-local-root.crt" -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
        $response = Invoke-WebRequest "$origin/health/ready" -TimeoutSec 15
        if ($response.StatusCode -ne 200) { throw 'Trusted HTTPS readiness failed.' }
        Save-State 'ReadyForBrowserVerification'
    } else {
        Save-State 'AwaitingWindowsTrust'
        Write-Host "To trust HTTPS for your Windows account, run: Import-Certificate -FilePath '$root/trust/caddy-local-root.crt' -CertStoreLocation Cert:\CurrentUser\Root"
        Write-Host "Then verify: Invoke-WebRequest '$origin/health/ready'"
    }
    Write-Host "Setup finished: $origin. Administrator: $($settings.AdminEmail)"
    Write-Host "Password file: $root/secrets/admin-password (never paste it into logs or chat)."
    Write-Host "Windows certificate trust requested: $($settings.TrustLocalCertificate). See installation.json for status."
} catch {
    Save-State 'Failed'
    if (Test-Path $composeFile) { & $docker.Source @compose stop proxy app worker *> $null }
    throw "Local setup failed at stage '$stage'. State and secrets are preserved at $root; public workloads were stopped. $($_.Exception.Message)"
}
