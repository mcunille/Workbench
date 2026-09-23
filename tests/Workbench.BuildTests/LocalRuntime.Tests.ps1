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

# GIVEN callers selecting an unused private ingress prefix for an isolated smoke project.
# WHEN binding the actual script parameter declarations, THEN defaults and private-range validation agree.
$smokeFile = Join-Path $PSScriptRoot '../../scripts/smoke-container.ps1'
$composeFile = Join-Path $PSScriptRoot '../../scripts/test-compose-runtime.ps1'
foreach ($contract in @(
    @{ Path = $smokeFile; Name = 'ComposeIngressPrefix'; Arguments = @{} },
    @{ Path = $composeFile; Name = 'IngressPrefix'; Arguments = @{ Image='fixture'; SqlNetwork='fixture'; SecretDirectory='fixture'; AdminPasswordFile='fixture' } }
)) {
    $tokens = $null; $parseErrors = $null
    $contractAst = [Management.Automation.Language.Parser]::ParseFile($contract.Path, [ref]$tokens, [ref]$parseErrors)
    Assert ($parseErrors.Count -eq 0) 'Smoke network script must parse.'
    Assert (@($contractAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq $contract.Name }).Count -eq 1) "Missing private ingress override $($contract.Name)."
    $binding = [scriptblock]::Create($contractAst.ParamBlock.Extent.Text + "`nreturn `$$($contract.Name)")
    $arguments = $contract.Arguments.Clone()
    Assert ((& $binding @arguments) -ceq '172.29') 'Existing Compose ingress default changed.'
    foreach ($prefix in @('172.16','172.29','172.30','172.31')) {
        $arguments[$contract.Name] = $prefix
        Assert ((& $binding @arguments) -ceq $prefix) 'Private ingress override was not retained.'
    }
    foreach ($prefix in @('172.15','172.32','10.0','172.030','172.30.1','172.30/16',"172.30`n")) {
        $arguments[$contract.Name] = $prefix
        $rejected = $false
        try { & $binding @arguments | Out-Null }
        catch [System.Management.Automation.ParameterBindingException] { $rejected = $true }
        Assert $rejected "Invalid ingress prefix was accepted: $prefix"
    }
}
# WHEN smoke forwards the choice and Compose derives its three related addresses,
# THEN proxy identity and IPAM settings share the explicit prefix without running Docker.
$smokeAst = [Management.Automation.Language.Parser]::ParseFile($smokeFile, [ref]$tokens, [ref]$parseErrors)
$forwarding = $smokeAst.Find({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and $node.Extent.Text.Contains('test-compose-runtime.ps1')
}, $true)
Assert ($forwarding.Extent.Text -match '-IngressPrefix\s+\$ComposeIngressPrefix') 'Smoke must forward its ingress choice.'
$composeAst = [Management.Automation.Language.Parser]::ParseFile($composeFile, [ref]$tokens, [ref]$parseErrors)
$settingsAst = $composeAst.Find({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$settings'
}, $true)
$settingsTable = $settingsAst.Find({ param($node) $node -is [Management.Automation.Language.HashtableAst] }, $true)
foreach ($setting in @{
    WORKBENCH_KNOWN_PROXY='172.30.123.2'
    WORKBENCH_INGRESS_SUBNET='172.30.123.0/24'
    WORKBENCH_INGRESS_DYNAMIC_RANGE='172.30.123.128/25'
}.GetEnumerator()) {
    $expression = @($settingsTable.KeyValuePairs | Where-Object { $_.Item1.SafeGetValue() -ceq $setting.Key })[0].Item2.Extent.Text
    $evaluate = [scriptblock]::Create('param($IngressPrefix,$octet)' + "`n" + $expression)
    Assert ((& $evaluate '172.30' 123) -ceq $setting.Value) "Ingress setting ignored the override: $($setting.Key)."
}
Write-Host 'Compose ingress override contracts passed.'
