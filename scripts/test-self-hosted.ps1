[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EnvironmentFile,
    [string]$DockerPath = 'docker'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = & $DockerPath compose --env-file $EnvironmentFile --file (Join-Path $repositoryRoot 'compose.yaml') config --format json 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Deployment configuration could not be resolved.' }
$config = ($output -join "`n") | ConvertFrom-Json
if ($config.services.app.image -notmatch '^.+@sha256:[a-f0-9]{64}$') { throw 'A release image pinned by SHA-256 digest is required.' }
$origin = $null
$settings = $config.services.app.environment
$network = $config.networks.ingress.ipam.config[0]
$subnet = [Net.IPNetwork]::Parse($network.subnet)
$dynamic = [Net.IPNetwork]::Parse($network.ip_range)
$proxy = [Net.IPAddress]::Parse($settings.WORKBENCH_KNOWN_PROXY)
if (-not $subnet.Contains($proxy) -or $dynamic.Contains($proxy) -or
    -not $subnet.Contains($dynamic.BaseAddress) -or $dynamic.PrefixLength -lt $subnet.PrefixLength) {
    throw 'The proxy must be inside the ingress subnet but outside its dynamic allocation range.'
}
if (-not [Uri]::TryCreate($settings.PublicOrigin, [UriKind]::Absolute, [ref]$origin) -or
    $origin.Scheme -ne 'https' -or $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.Fragment -or $origin.UserInfo -or
    $origin.Port -ne 443 -or $origin.Host -cne $settings.AllowedHosts) { throw 'The default TLS topology requires one matching HTTPS origin and DNS host on port 443.' }
foreach ($secret in $config.secrets.PSObject.Properties) {
    if (-not (Test-Path -LiteralPath $secret.Value.file -PathType Leaf)) { throw "Required secret file is missing: $($secret.Name)." }
}
Write-Host 'Self-hosted static preflight passed. TLS, SQL, SMTP, persistence, and rollback still require a live acceptance drill.'
