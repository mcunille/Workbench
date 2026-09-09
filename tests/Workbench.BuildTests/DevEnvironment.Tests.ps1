$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../../scripts/local-self-host/Common.ps1"
. "$PSScriptRoot/../../scripts/local-self-host/Configuration.ps1"
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action | Out-Null } catch { $failed = $true }; Assert $failed 'Expected rejection.' }

# GIVEN existing self-host mounts and connections, WHEN built, THEN preserve fail-closed mounts and validated TLS.
$mount = New-SetupMount 'C:/fixture' '/run/secrets/input'
Assert ($mount.read_only -and -not $mount.bind.create_host_path) 'Bind mounts must not create missing secret files.'
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection.set_ConnectionString((New-LocalConnection web 'test;quoted"value'))
Assert ($connection['Password'] -ceq 'test;quoted"value' -and $connection['TrustServerCertificate'] -eq 'False') 'Existing connection contract changed.'

if (Test-Path "$PSScriptRoot/../../scripts/dev-environment/State.ps1") { . "$PSScriptRoot/../../scripts/dev-environment/State.ps1" }
# GIVEN a saved environment copied to another checkout, WHEN authority is checked, THEN reject it before Docker calls.
Assert ([bool](Get-Command Assert-DevOwner -ErrorAction SilentlyContinue)) 'Per-worktree ownership validation is missing.'
$owner = @{ Root='C:/fixture/a'; GitDirectory='C:/git/worktrees/a' }
$state = @{ Version=1; EnvironmentId='dev-0123456789abcdef0123456789abcdef'; Owner=$owner; Resources=@{} }
Assert-DevOwner $state $owner
Reject { Assert-DevOwner $state @{ Root='C:/fixture/b'; GitDirectory='C:/git/worktrees/b' } }
# GIVEN corrupted IDs or schema versions, WHEN loading state, THEN refuse unsafe resource authority.
foreach ($id in @('dev-../bad','DEV-0123456789abcdef0123456789abcdef','')) {
    $bad = $state.Clone(); $bad.EnvironmentId = $id; Reject { Assert-DevOwner $bad $owner }
}
$bad = $state.Clone(); $bad.Version = 2; Reject { Assert-DevOwner $bad $owner }

# GIVEN matching Docker labels, WHEN validating a resource, THEN require every ownership dimension.
$labels = Get-DevLabels $state
Assert-DevResourceLabels $state $labels
foreach ($key in @('workbench.purpose','workbench.environment','workbench.owner')) {
    $bad = $labels.Clone(); $bad[$key] = 'foreign'; Reject { Assert-DevResourceLabels $state $bad }
}
if (Test-Path "$PSScriptRoot/../../scripts/dev-environment/Compose.ps1") { . "$PSScriptRoot/../../scripts/dev-environment/Compose.ps1" }
# GIVEN a development preview, WHEN generating Compose, THEN SQL is private and app credentials are least privilege.
Assert ([bool](Get-Command New-DevCompose -ErrorAction SilentlyContinue)) 'Development Compose generation is missing.'
$context = @{ State=$state; Root='C:/fixture/a/.dev-environment' }
$state.Image = 'sha256:' + ('a' * 64); $state.Port = 0; $state.InstallationId = [Guid]::NewGuid().ToString()
$config = New-DevCompose $context
Assert (-not $config.services.sql.ports) 'SQL must not publish a host port.'
Assert ($config.services.app.ports[0].host_ip -ceq '127.0.0.1') 'App must bind loopback.'
Assert ($config.services.app.environment.Development__EnvironmentId -ceq $state.EnvironmentId) 'Browser isolation missing.'
Assert ($config.services.app.volumes.source -notmatch '(setup|operator|migrator)-connection') 'Privileged runtime credentials.'
foreach ($resource in @($config.services.app,$config.services.sql,$config.volumes.blobs,$config.volumes.'sql-data',$config.networks.dependencies)) { Assert-DevResourceLabels $state $resource.labels }
if (Test-Path "$PSScriptRoot/../../scripts/dev-environment/Source.ps1") { . "$PSScriptRoot/../../scripts/dev-environment/Source.ps1" }
# GIVEN dirty and untracked build inputs, WHEN fingerprinting, THEN track source edits but exclude secrets and generated outputs.
Assert ([bool](Get-Command Get-DevSource -ErrorAction SilentlyContinue)) 'Current-source preview fingerprinting is missing.'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('workbench-dev-source-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force "$fixture/src/Workbench.Server/bin", "$fixture/.dev-environment" | Out-Null
    git -C $fixture init --quiet
    Set-Content "$fixture/Dockerfile" 'FROM scratch'
    Set-Content "$fixture/src/Workbench.Server/code.cs" 'before'
    Set-Content "$fixture/src/Workbench.Server/bin/ignored" 'generated'
    Set-Content "$fixture/.dev-environment/secret" 'private'
    $first = Get-DevSource $fixture
    Set-Content "$fixture/src/Workbench.Server/code.cs" 'after'
    $second = Get-DevSource $fixture
    Assert ($first.Hash -cne $second.Hash) 'Untracked source edit omitted.'
    Set-Content "$fixture/.dev-environment/secret" 'changed-private'
    Set-Content "$fixture/src/Workbench.Server/bin/ignored" 'changed-generated'
    Assert ($second.Hash -ceq (Get-DevSource $fixture).Hash) 'Generated or private input included.'
    # GIVEN a held OS file lock, WHEN another operation enters, THEN it times out without stealing ownership.
    $held = Enter-DevLock $fixture
    try { Reject { Enter-DevLock $fixture -TimeoutSeconds 0 } } finally { $held.Dispose() }
    # WHEN its owner releases the lock THEN the next operation can acquire it.
    $next = Enter-DevLock $fixture -TimeoutSeconds 0
    $next.Dispose()
} finally {
    if ([IO.Path]::GetFileName($fixture) -notmatch '^workbench-dev-source-[a-f0-9]{32}$' -or
        [IO.Path]::GetDirectoryName($fixture) -ne [IO.Path]::GetTempPath().TrimEnd('\','/')) { throw 'Unexpected fixture path.' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
Write-Host 'Development environment contracts passed.'
