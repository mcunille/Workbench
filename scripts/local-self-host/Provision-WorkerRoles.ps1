# Copyright (c) 2026 The White Stag Collection.
[CmdletBinding()]
param(
    [string]$InstallationRoot = (Join-Path $env:LOCALAPPDATA 'WorkbenchSelfHost'),
    [Parameter(Mandatory)][string]$Network
)

$ErrorActionPreference = 'Stop'
$secretsRoot = Join-Path $InstallationRoot 'secrets'
$trustBundle = Join-Path $InstallationRoot 'trust/ca-certificates.crt'
$sqlImage = 'mcr.microsoft.com/mssql/server@sha256:7c29dfbac885ad7519e219c7fe4aee0e67283e21a10e9c252d13b0fbde1866f8'
$required = @('sql-bootstrap-password', 'worker-password', 'maintenance-password')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $secretsRoot $name) -PathType Leaf)) {
        throw "Required credential file is missing: $name"
    }
}
if (-not (Test-Path -LiteralPath $trustBundle -PathType Leaf)) { throw 'Trust bundle missing.' }
$docker = Get-Command docker -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $docker -and $IsWindows) {
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Programs/DockerDesktop/resources/bin/docker.exe'),
        'C:/Program Files/Docker/Docker/resources/bin/docker.exe'
    )) {
        if (Test-Path -LiteralPath $candidate) { $docker = Get-Command $candidate; break }
    }
}
if (-not $docker) { throw 'Docker CLI missing.' }

$sql = @'
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF DB_NAME() <> N'Workbench'
    THROW 50000, 'Wrong provisioning database.', 1;
IF DATABASE_PRINCIPAL_ID(N'workbench_worker') IS NULL OR
   DATABASE_PRINCIPAL_ID(N'workbench_storage_maintenance') IS NULL
    THROW 50000, 'Required roles are missing; verify migrations.', 1;
IF USER_ID(N'workbench_worker_local') IS NOT NULL OR
   USER_ID(N'workbench_maintenance_local') IS NOT NULL
    THROW 50000, 'A target user already exists; inspect before provisioning.', 1;
CREATE USER [workbench_worker_local] WITH PASSWORD=N'$(WB_WORKER_PASSWORD)';
ALTER ROLE [workbench_worker] ADD MEMBER [workbench_worker_local];
CREATE USER [workbench_maintenance_local] WITH PASSWORD=N'$(WB_MAINTENANCE_PASSWORD)';
ALTER ROLE [workbench_storage_maintenance] ADD MEMBER [workbench_maintenance_local];
COMMIT;
SELECT u.name AS DatabaseUser, r.name AS DatabaseRole
FROM sys.database_role_members AS m
JOIN sys.database_principals AS u ON u.principal_id=m.member_principal_id
JOIN sys.database_principals AS r ON r.principal_id=m.role_principal_id
WHERE u.name IN (N'workbench_worker_local', N'workbench_maintenance_local')
ORDER BY u.name;
'@
$command = @'
export SQLCMDPASSWORD="$(cat /run/secrets/sql-bootstrap-password)"
export WB_WORKER_PASSWORD="$(cat /run/secrets/worker-password)"
export WB_MAINTENANCE_PASSWORD="$(cat /run/secrets/maintenance-password)"
# SQLCMD substitutes inside SQL literals; restrict inputs to this walkthrough's generated format.
if [[ ! "$WB_WORKER_PASSWORD" =~ ^Wb-[A-F0-9]{64}-aA9!$ ]] || [[ ! "$WB_MAINTENANCE_PASSWORD" =~ ^Wb-[A-F0-9]{64}-aA9!$ ]]; then
    echo 'Unexpected password format; no SQL changes made.' >&2
    exit 1
fi
exec /opt/mssql-tools18/bin/sqlcmd -S tcp:sql,1433 -U sa -d Workbench -N -b -l 5 -t 30 -i /run/setup/provision.sql
'@
$temporaryRoot = Join-Path $secretsRoot ('role-provision-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $sqlPath = Join-Path $temporaryRoot 'provision.sql'
    [IO.File]::WriteAllText($sqlPath, $sql)
    $dockerArgs = @(
        'run', '--rm', '--pull', 'never',
        '--network', $Network,
        '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
        '--env', 'SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt',
        '--mount', "type=bind,source=$trustBundle,target=/etc/ssl/certs/ca-certificates.crt,readonly",
        '--mount', "type=bind,source=$sqlPath,target=/run/setup/provision.sql,readonly"
    )
    foreach ($name in $required) {
        $path = Join-Path $secretsRoot $name
        $dockerArgs += @('--mount', "type=bind,source=$path,target=/run/secrets/$name,readonly")
    }
    $dockerArgs += @('--entrypoint', '/bin/bash', $sqlImage, '-ec', $command)
    & $docker.Source @dockerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Worker/maintenance provisioning failed; inspect before retrying.' }
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporaryRoot)
    if ((Split-Path $resolved -Parent) -ne [IO.Path]::GetFullPath($secretsRoot) -or
        (Split-Path $resolved -Leaf) -notmatch '^role-provision-[a-f0-9]{32}$') {
        throw 'Refusing unexpected temporary path cleanup.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
