[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$worktreeEnvironment = Join-Path $repositoryRoot '.env.dev'
$environmentFile = if (Test-Path -LiteralPath $worktreeEnvironment) {
    $worktreeEnvironment
}
elseif (-not [string]::IsNullOrWhiteSpace($env:WORKBENCH_DEV_ENV_FILE)) {
    $env:WORKBENCH_DEV_ENV_FILE
}
else {
    throw 'Create .env.dev from .env.dev.example or set WORKBENCH_DEV_ENV_FILE.'
}

if (-not (Test-Path -LiteralPath $environmentFile -PathType Leaf)) {
    throw 'The configured Workbench development environment file does not exist.'
}

foreach ($line in Get-Content -LiteralPath $environmentFile) {
    $trimmed = $line.Trim()
    if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) {
        continue
    }

    $separator = $trimmed.IndexOf('=')
    if ($separator -le 0) {
        throw 'The Workbench development environment file contains an invalid line.'
    }

    $name = $trimmed.Substring(0, $separator).Trim()
    $value = $trimmed.Substring($separator + 1)
    if ($name -notmatch '^WORKBENCH_[A-Z0-9_]+$') {
        throw 'The Workbench development environment file contains an invalid variable name.'
    }

    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}

$requiredNames = @(
    'WORKBENCH_SQL_HOST',
    'WORKBENCH_DATABASE',
    'WORKBENCH_SETUP_SQL_USER',
    'WORKBENCH_SETUP_SQL_PASSWORD',
    'WORKBENCH_WEB_SQL_USER',
    'WORKBENCH_WEB_SQL_PASSWORD',
    'WORKBENCH_OPERATOR_SQL_USER',
    'WORKBENCH_OPERATOR_SQL_PASSWORD',
    'WORKBENCH_MIGRATOR_SQL_USER',
    'WORKBENCH_MIGRATOR_SQL_PASSWORD',
    'WORKBENCH_TENANT_CONTEXT_PROOF_KEY',
    'WORKBENCH_DEV_TENANT_NAME',
    'WORKBENCH_DEV_ADMIN_EMAIL',
    'WORKBENCH_DEV_ADMIN_PASSWORD'
)
foreach ($name in $requiredNames) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name, 'Process'))) {
        throw "Required Workbench development setting '$name' is missing."
    }
}

$env:WORKBENCH_WEB_CONNECTION = "Server=$env:WORKBENCH_SQL_HOST;Database=$env:WORKBENCH_DATABASE;User Id=$env:WORKBENCH_WEB_SQL_USER;Password=$env:WORKBENCH_WEB_SQL_PASSWORD;Encrypt=True;TrustServerCertificate=True"

Write-Host 'Workbench development environment loaded.'
