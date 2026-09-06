[CmdletBinding()]
param(
    [string]$DockerPath = 'docker'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$originalProxy = [Environment]::GetEnvironmentVariable('WORKBENCH_KNOWN_PROXY', 'Process')
$emptyEnvironment = [IO.Path]::GetTempFileName()
try {
    foreach ($proxy in @($null, '', '192.0.2.1', '2001:db8::1')) {
        # GIVEN an absent, empty, or explicit immediate proxy, without a local .env fallback.
        [Environment]::SetEnvironmentVariable('WORKBENCH_KNOWN_PROXY', $proxy, 'Process')
        # WHEN Compose resolves the production service configuration.
        $output = & $DockerPath compose --env-file $emptyEnvironment --file (Join-Path $repositoryRoot 'compose.yaml') config --format json 2>&1
        $exitCode = $LASTEXITCODE
        if ([string]::IsNullOrEmpty($proxy)) {
            # THEN missing and empty proxy values must fail before any container starts.
            if ($exitCode -eq 0) { throw 'Compose accepted a missing or empty trusted proxy.' }
            if (($output -join "`n") -notmatch 'WORKBENCH_KNOWN_PROXY') {
                throw 'Compose failed for a reason other than the required proxy.'
            }
        } else {
            # THEN an explicit IPv4 or IPv6 peer remains unchanged.
            if ($exitCode -ne 0) { throw 'Compose rejected an explicit trusted proxy.' }
            $configuration = ($output -join "`n") | ConvertFrom-Json
            if ($configuration.services.app.environment.WORKBENCH_KNOWN_PROXY -cne $proxy) {
                throw 'Compose changed the explicit trusted proxy.'
            }
        }
    }
    Write-Host 'Compose rejects absent/empty proxy configuration and preserves explicit IPv4/IPv6 peers.'
} finally {
    [Environment]::SetEnvironmentVariable('WORKBENCH_KNOWN_PROXY', $originalProxy, 'Process')
    Remove-Item -LiteralPath $emptyEnvironment -Force
}
