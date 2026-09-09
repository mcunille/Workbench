# Copyright (c) 2026 The White Stag Collection.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../../scripts/local-self-host/Common.ps1"
. "$PSScriptRoot/../../scripts/local-self-host/Configuration.ps1"
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-LocalDocker($Docker, [string[]]$Arguments) { $script:captured = $Arguments; return '{"fixture":true}' }

# GIVEN existing self-host defaults, WHEN building connections, THEN TLS, quoting and workload pools remain unchanged.
foreach ($role in @('setup','web','worker','operator','migrator')) {
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connection.set_ConnectionString((New-LocalConnection $role 'fixture;quoted"password'))
    $user = if ($role -eq 'setup') { 'sa' } else { "workbench_${role}_local" }
    $pool = if ($role -in @('web','worker')) { 20 } else { 5 }
    Assert ($connection['User ID'] -ceq $user -and $connection['Max Pool Size'] -eq $pool) 'Self-host identity or pooling changed.'
    Assert ($connection['Server'] -ceq 'tcp:sql,1433' -and $connection['Database'] -ceq 'Workbench') 'Self-host destination changed.'
    Assert ($connection['Encrypt'] -eq 'True' -and $connection['TrustServerCertificate'] -eq 'False' -and $connection['Persist Security Info'] -eq 'False') 'Self-host TLS or secret handling changed.'
    Assert ($connection['Password'] -ceq 'fixture;quoted"password' -and $connection['Connect Timeout'] -eq 15) 'Connection escaping or timeout changed.'
}

# GIVEN a self-host database command, WHEN invoked, THEN retain hardening, trust, scoped mounts and quiet stdout.
$context = @{ Docker=@{ Source='unused' }; Root='C:/fixture'; Project='fixture'; Image='sha256:fixture' }
$output = Invoke-LocalDatabase $context operator @('bootstrap') @('admin-password') @('--mount','fixture-extra')
Assert ($null -eq $output) 'Self-host wrapper must discard stdout.'
$expected = @('run','--rm','--pull','never','--network','fixture_dependencies','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true',
    '--tmpfs','/tmp:rw,noexec,nosuid,size=64m,uid=1654,gid=1654','--env','SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt',
    '--mount','type=bind,source=C:/fixture/trust/ca-certificates.crt,target=/etc/ssl/certs/ca-certificates.crt,readonly',
    '--mount','type=bind,source=C:/fixture/secrets/operator-connection,target=/run/secrets/connection,readonly',
    '--mount','type=bind,source=C:/fixture/secrets/admin-password,target=/run/secrets/admin-password,readonly',
    '--mount','fixture-extra','--entrypoint','dotnet','sha256:fixture','/opt/workbench/database/Workbench.Database.dll','bootstrap',
    '--connection-file','/run/secrets/connection','--expected-database','Workbench')
Assert (($captured -join "`n") -ceq ($expected -join "`n")) 'Self-host database invocation changed.'

# GIVEN a worktree preview, WHEN invoking inspection, THEN use its owned network and preserve JSON stdout without a trust mount.
$context.Network = 'owned-network'
$output = Invoke-DatabaseContainer $context setup @('development','inspect','--environment','Development') @() @('--label','owner=fixture','--name','owned-job')
Assert ($output -ceq '{"fixture":true}') 'Shared invocation must return stdout.'
Assert ($captured[5] -ceq 'owned-network') 'Explicit network was ignored.'
Assert (-not ($captured -match 'SSL_CERT_FILE|ca-certificates')) 'Preview unexpectedly requires a self-host trust bundle.'
Assert (($captured -contains 'owner=fixture') -and ($captured -contains 'owned-job')) 'Caller container identity was omitted.'
Assert (($captured -contains '--read-only') -and ($captured -contains 'no-new-privileges:true')) 'Preview container hardening was lost.'
Assert (($captured -contains 'development') -and ($captured -contains 'inspect') -and ($captured -contains 'Development')) 'Database command arguments were omitted.'

# GIVEN explicit development SQL settings, WHEN serializing, THEN preserve the target, credentials and deliberate TLS choice.
$connection.set_ConnectionString((New-WorkbenchSqlConnection 'tcp:preview-sql,1433' 'FixtureDb' 'fixture_user' 'fixture;quoted"password' $true 7))
Assert ($connection['Server'] -ceq 'tcp:preview-sql,1433' -and $connection['Database'] -ceq 'FixtureDb' -and $connection['User ID'] -ceq 'fixture_user') 'Explicit SQL destination was ignored.'
Assert ($connection['TrustServerCertificate'] -eq 'True' -and $connection['Encrypt'] -eq 'True' -and $connection['Max Pool Size'] -eq 7) 'Explicit SQL connection settings were ignored.'
Assert ($connection['Password'] -ceq 'fixture;quoted"password') 'SQL password was not safely quoted.'
$connection.set_ConnectionString((New-WorkbenchSqlConnection 'sql' 'FixtureDb' 'fixture_user' 'fixture-password'))
Assert ($connection['TrustServerCertificate'] -eq 'False' -and $connection['Max Pool Size'] -eq 5) 'Shared SQL defaults are unsafe or changed.'
Write-Host 'Shared local runtime contracts passed.'
