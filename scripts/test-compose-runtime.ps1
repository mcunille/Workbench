[CmdletBinding()]
param(
    [string]$DockerPath = 'docker',
    [Parameter(Mandatory)][string]$Image,
    [Parameter(Mandatory)][string]$SqlNetwork,
    [Parameter(Mandatory)][string]$SecretDirectory,
    [Parameter(Mandatory)][string]$AdminPasswordFile,
    [string]$AdminEmail = 'smoke-admin@example.test'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = 'workbench-compose-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$fixture = Join-Path ([IO.Path]::GetTempPath()) $project
New-Item -ItemType Directory -Path $fixture | Out-Null
$environmentFile = Join-Path $fixture 'deployment.env'
$overrideFile = Join-Path $fixture 'override.yaml'
$caddyFile = Join-Path $fixture 'Caddyfile'
$caFile = Join-Path $fixture 'root.crt'
$compose = @('compose', '--project-name', $project, '--env-file', $environmentFile, '--file', (Join-Path $repositoryRoot 'compose.yaml'), '--file', $overrideFile)
$client = $null
$values = @{}
function Invoke-Compose([string[]]$Arguments) {
    $result = & $DockerPath @compose @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        # This fixture supplies secrets only as files; lifecycle output contains no secret values.
        throw "Compose runtime operation failed: $($Arguments[0]). $($result -join [Environment]::NewLine)"
    }
    return $result
}
function Wait-Ready {
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        try {
            $response = $client.Send('GET', '/health/ready', $null, $null)
            $status = [int]$response.StatusCode
            $response.Dispose()
            if ($status -eq 200) { return }
        } catch { }
        Start-Sleep -Seconds 2
    }
    throw 'Compose TLS readiness did not succeed.'
}
try {
    foreach ($name in @('web-connection', 'worker-connection', 'tenant-proof', 'data-protection.pfx', 'certificate-password', 'smtp-password')) {
        if (-not (Test-Path -LiteralPath (Join-Path $SecretDirectory $name) -PathType Leaf)) { throw "Missing Compose fixture secret: $name." }
    }
    $octet = Get-Random -Minimum 100 -Maximum 240
    $settings = @{
        WORKBENCH_IMAGE = $Image
        WORKBENCH_INSTALLATION_ID = [Guid]::NewGuid().ToString()
        WORKBENCH_PUBLIC_HOST = 'localhost'
        WORKBENCH_PUBLIC_ORIGIN = 'https://localhost'
        WORKBENCH_SECRET_DIRECTORY = ([IO.Path]::GetFullPath($SecretDirectory)).Replace('\', '/')
        WORKBENCH_KNOWN_PROXY = "172.29.$octet.2"
        WORKBENCH_INGRESS_SUBNET = "172.29.$octet.0/24"
        WORKBENCH_INGRESS_DYNAMIC_RANGE = "172.29.$octet.128/25"
        WORKBENCH_STORAGE_PROVIDER = 'FileSystem'
        WORKBENCH_BLOB_CONTAINER_URI = ''
        WORKBENCH_DELIVERY_PROVIDER = 'Disabled'
        WORKBENCH_PUBLIC_RECOVERY_ENABLED = 'false'
        WORKBENCH_PUBLIC_INVITATION_ENABLED = 'false'
    }
    foreach ($name in $settings.Keys) {
        $values[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    Set-Content $environmentFile ''
    $proxyConfig = Get-Content -LiteralPath (Join-Path $repositoryRoot 'infra/compose/Caddyfile') -Raw
    $proxyConfig = $proxyConfig.Replace('admin off', "admin off`n    auto_https disable_redirects")
    $proxyConfig = $proxyConfig.Replace('{$WORKBENCH_PUBLIC_HOST} {', '{$WORKBENCH_PUBLIC_HOST} {' + "`n    tls internal")
    Set-Content -LiteralPath $caddyFile -Value $proxyConfig
    $quotedCaddy = ($caddyFile.Replace('\', '/') | ConvertTo-Json -Compress)
    $quotedNetwork = ($SqlNetwork | ConvertTo-Json -Compress)
    @"
services:
  proxy:
    ports: !override
      - target: 443
        host_ip: 127.0.0.1
        published: "0"
        protocol: tcp
    volumes:
      - type: bind
        source: $quotedCaddy
        target: /etc/caddy/Caddyfile
        read_only: true
networks:
  dependencies:
    external: true
    name: $quotedNetwork
"@ | Set-Content $overrideFile
    # GIVEN the checked topology with local TLS/network/image overrides.
    # WHEN services start, THEN only the TLS proxy publishes a host listener.
    Invoke-Compose -Arguments @('up', '--detach', 'app', 'worker', 'proxy') | Out-Null
    $port = (Invoke-Compose -Arguments @('port', 'proxy', '443') | Select-Object -Last 1).ToString().Trim().Split(':')[-1]
    $proxy = (Invoke-Compose -Arguments @('ps', '--quiet', 'proxy') | Select-Object -Last 1).ToString().Trim()
    $copied = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        & $DockerPath cp "${proxy}:/data/caddy/pki/authorities/local/root.crt" $caFile *> $null
        if ($LASTEXITCODE -eq 0) { $copied = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $copied) { throw 'Caddy did not create its local test CA.' }
    if (-not ('WorkbenchComposeTlsClient' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
public sealed class WorkbenchComposeTlsClient : IDisposable
{
    private readonly HttpClient client;
    private readonly X509Certificate2 root;
    public WorkbenchComposeTlsClient(string origin, string rootFile)
    {
        root = X509Certificate2.CreateFromPem(System.IO.File.ReadAllText(rootFile));
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (request, certificate, suppliedChain, errors) => Validate(certificate, suppliedChain, errors);
        client = new HttpClient(handler) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(8) };
    }
    private bool Validate(X509Certificate2 certificate, X509Chain suppliedChain, SslPolicyErrors errors)
    {
            if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 ||
                (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            if (suppliedChain != null)
                foreach (var element in suppliedChain.ChainElements) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            return chain.Build(certificate);
    }
    public int UnknownHostStatus()
    {
        using var socket = new System.Net.Sockets.TcpClient();
        socket.Connect(client.BaseAddress.Host, client.BaseAddress.Port);
        using var stream = new SslStream(socket.GetStream(), false, (sender, certificate, chain, errors) =>
            Validate(certificate as X509Certificate2, chain, errors));
        stream.ReadTimeout = 8000;
        stream.WriteTimeout = 8000;
        stream.AuthenticateAsClient(client.BaseAddress.Host);
        var bytes = Encoding.ASCII.GetBytes("GET /api/system HTTP/1.1\r\nHost: attacker.example\r\nConnection: close\r\n\r\n");
        stream.Write(bytes);
        using var reader = new System.IO.StreamReader(stream);
        return int.Parse(reader.ReadLine().Split(' ')[1]);
    }
    public HttpResponseMessage Send(string method, string path, string body, string csrf)
        => SendForwarded(method, path, body, csrf, false);
    public HttpResponseMessage SendForwarded(string method, string path, string body, string csrf, bool forged)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (csrf != null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (forged) {
            request.Headers.Add("X-Forwarded-For", "203.0.113.99, 10.42.0.2");
            request.Headers.Add("X-Forwarded-Proto", "http");
            request.Headers.Add("X-Forwarded-Host", "attacker.example");
        }
        return client.SendAsync(request).GetAwaiter().GetResult();
    }
    public void Dispose() { client.Dispose(); root.Dispose(); }
}
'@
    }
    $client = [WorkbenchComposeTlsClient]::new("https://localhost:$port", $caFile)
    Wait-Ready
    $antiforgery = $client.Send('GET', '/api/auth/antiforgery', $null, $null)
    if ([int]$antiforgery.StatusCode -ne 200) { throw 'Compose antiforgery request failed.' }
    $token = ($antiforgery.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).requestToken
    $antiforgery.Dispose()
    # WHEN login succeeds through TLS, THEN its session survives app replacement.
    $body = @{ email = $AdminEmail; password = ([IO.File]::ReadAllText($AdminPasswordFile).TrimEnd("`r", "`n")) } | ConvertTo-Json -Compress
    $login = $client.Send('POST', '/api/auth/login', $body, $token)
    $body = $null
    if ([int]$login.StatusCode -ne 204) { throw 'Compose TLS login failed.' }
    $cookies = $login.Headers.GetValues('Set-Cookie') -join ';'
    if ($cookies -notmatch '__Host-Workbench.Session=' -or $cookies -notmatch '(?i)secure') { throw 'Compose login did not issue a secure session cookie.' }
    $login.Dispose()
    foreach ($phase in @('before', 'after')) {
        if ($phase -eq 'after') {
            Invoke-Compose -Arguments @('up', '--detach', '--force-recreate', '--no-deps', 'app') | Out-Null
            Wait-Ready
        }
        $identity = $client.Send('GET', '/api/auth/me', $null, $null)
        if ([int]$identity.StatusCode -ne 200) { throw "Compose session failed $phase app replacement." }
        $identity.Dispose()
    }
    $forged = $client.SendForwarded('GET', '/api/auth/me', $null, $null, $true)
    if ([int]$forged.StatusCode -ne 200) { throw 'Forged forwarding headers disrupted the canonical authenticated route.' }
    $forged.Dispose()
    $unknownHost = $client.UnknownHostStatus()
    if ($unknownHost -notin @(400, 404, 421)) { throw 'Unknown HTTP Host reached the canonical application route.' }
    $app = (Invoke-Compose -Arguments @('ps', '--quiet', 'app') | Select-Object -Last 1).ToString().Trim()
    $binding = & $DockerPath inspect --format '{{json .HostConfig.PortBindings}}' $app
    if ($LASTEXITCODE -ne 0 -or $binding -notin @('{}', 'null')) { throw 'Compose app published a host listener.' }
    $worker = (Invoke-Compose -Arguments @('ps', '--quiet', 'worker') | Select-Object -Last 1).ToString().Trim()
    if (-not $worker) { throw 'Compose worker did not stay running.' }
    $queueObserved = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $workerLogs = & $DockerPath logs $worker 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect worker operational status.' }
        foreach ($line in $workerLogs) {
            try { $event = $line.ToString() | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($event.Event -eq 'WorkQueueStatus' -and $null -ne $event.PendingCount -and
                $null -ne $event.OldestPendingAgeSeconds -and $event.PendingCount -ge 0 -and $event.OldestPendingAgeSeconds -ge 0) {
                $queueObserved = $true; break
            }
        }
        if ($queueObserved) { break }
        Start-Sleep -Seconds 2
    }
    $workerState = & $DockerPath inspect --format '{{.State.Running}} {{.RestartCount}}' $worker
    if ($LASTEXITCODE -ne 0 -or $workerState.Trim() -ne 'true 0' -or -not $queueObserved) {
        throw 'Compose worker did not demonstrate queue access without restarting.'
    }
    Write-Host 'Local Compose passed: validated internal TLS, SQL readiness, Secure-cookie login, durable session after app replacement, forwarding-header smoke, private app listener, and worker queue telemetry without restarts. Public CA issuance and SMTP delivery remain untested.'
} finally {
    if ($client) { $client.Dispose() }
    if ((Test-Path $environmentFile) -and (Test-Path $overrideFile)) {
        & $DockerPath @compose down --volumes --remove-orphans *> $null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Cleanup failed for isolated Compose project $project." }
    }
    foreach ($name in $values.Keys) { [Environment]::SetEnvironmentVariable($name, $values[$name], 'Process') }
    $resolved = [IO.Path]::GetFullPath($fixture)
    $expected = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $project))
    if ($resolved -eq $expected -and (Split-Path $resolved -Leaf) -eq $project) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
