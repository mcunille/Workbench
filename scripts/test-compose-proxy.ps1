[CmdletBinding()]
param([string]$DockerPath = 'docker')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$values = @{
    WORKBENCH_INSTALLATION_ID = '79289486-b55b-43d4-9dd7-259ff3c4a634'
    WORKBENCH_KNOWN_PROXY = '172.30.74.2'
    WORKBENCH_INGRESS_SUBNET = '172.30.74.0/24'
    WORKBENCH_INGRESS_DYNAMIC_RANGE = '172.30.74.128/25'
    WORKBENCH_PUBLIC_HOST = 'workbench.example.com'
    WORKBENCH_PUBLIC_ORIGIN = 'https://workbench.example.com'
    WORKBENCH_IMAGE = 'example.invalid/workbench@sha256:' + ('a' * 64)
    WORKBENCH_SECRET_DIRECTORY = './artifacts/compose-test-secrets'
    WORKBENCH_SQL_EDITION = 'Developer'
}
$original = @{}
$emptyEnvironment = [IO.Path]::GetTempFileName()
try {
    foreach ($key in $values.Keys) {
        $original[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $values[$key], 'Process')
    }
    # GIVEN a complete production configuration using file references only.
    # WHEN Compose normalizes the service definitions without starting containers.
    $output = & $DockerPath compose --env-file $emptyEnvironment --file (Join-Path $repositoryRoot 'compose.yaml') config --format json 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Compose rejected complete production configuration.' }
    $config = ($output -join "`n") | ConvertFrom-Json
    # THEN app allocation cannot steal the fixed ingress peer address before the proxy starts.
    $dynamic = [Net.IPNetwork]::Parse($config.networks.ingress.ipam.config[0].ip_range)
    if ($dynamic.Contains([Net.IPAddress]::Parse($values.WORKBENCH_KNOWN_PROXY))) { throw 'Proxy IP overlaps dynamic container allocation.' }
    # THEN TLS is the sole public entrypoint and each workload gets only its SQL credential.
    if ($config.services.app.ports) { throw 'App listener is exposed on the host.' }
    if (-not $config.services.proxy.ports) { throw 'TLS proxy has no published listener.' }
    if ($config.services.worker.image -cne $config.services.app.image) { throw 'Worker and web images differ.' }
    foreach ($service in @('app', 'worker')) {
        $runtime = $config.services.$service
        if (-not $runtime.read_only -or $runtime.user -ne '1654:1654') { throw 'Runtime hardening is absent.' }
        if ($runtime.environment.PublicOrigin -cne $values.WORKBENCH_PUBLIC_ORIGIN) { throw 'Canonical origin was changed.' }
        $sources = @($runtime.secrets | ForEach-Object source)
        $forbidden = if ($service -eq 'app') { 'worker-connection' } else { 'web-connection' }
        if ($sources -contains $forbidden) { throw 'Runtime received another workload SQL credential.' }
    }
    if ($config.services.app.environment.WORKBENCH_KNOWN_PROXY -cne $values.WORKBENCH_KNOWN_PROXY -or
        $config.services.proxy.networks.ingress.ipv4_address -cne $values.WORKBENCH_KNOWN_PROXY) { throw 'Proxy trust and topology differ.' }
    if ($config.services.app.environment.AllowedHosts -cne $values.WORKBENCH_PUBLIC_HOST) { throw 'Public host is not explicit.' }
    # GIVEN the optional SQL profile and explicit disposable-test edition.
    # WHEN both files are normalized, THEN SQL has private networking and its own secret only.
    $sqlOutput = & $DockerPath compose --env-file $emptyEnvironment --file (Join-Path $repositoryRoot 'compose.yaml') --file (Join-Path $repositoryRoot 'infra/compose/local-sql.yaml') --profile local-sql config --format json 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Compose rejected the optional SQL profile.' }
    $sql = (($sqlOutput -join "`n") | ConvertFrom-Json).services.sql
    if ($sql.ports -or @($sql.secrets).Count -ne 1 -or $sql.secrets[0].source -ne 'sql-bootstrap-password') { throw 'Local SQL credential/network isolation failed.' }
    foreach ($key in @('WORKBENCH_KNOWN_PROXY', 'WORKBENCH_INSTALLATION_ID', 'WORKBENCH_PUBLIC_HOST', 'WORKBENCH_PUBLIC_ORIGIN', 'WORKBENCH_IMAGE', 'WORKBENCH_SECRET_DIRECTORY')) {
        foreach ($missing in @($null, '')) {
            # GIVEN an absent or empty required deployment setting.
            [Environment]::SetEnvironmentVariable($key, $missing, 'Process')
            # WHEN configuration is resolved, THEN it fails before containers start.
            $output = & $DockerPath compose --env-file $emptyEnvironment --file (Join-Path $repositoryRoot 'compose.yaml') config --format json 2>&1
            if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch $key) { throw "Compose did not reject missing $key." }
        }
        [Environment]::SetEnvironmentVariable($key, $values[$key], 'Process')
    }
    Write-Host 'Compose production topology, credential separation, and required configuration checks passed (no containers started).'
} finally {
    foreach ($key in $original.Keys) { [Environment]::SetEnvironmentVariable($key, $original[$key], 'Process') }
    Remove-Item -LiteralPath $emptyEnvironment -Force
}
