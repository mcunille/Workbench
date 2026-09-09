# Shared host/container primitives used by local self-host and worktree previews.
function Get-LocalDocker {
    $docker = Get-Command docker -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $docker) {
        foreach ($candidate in @("$env:LOCALAPPDATA/Programs/DockerDesktop/resources/bin/docker.exe", 'C:/Program Files/Docker/Docker/resources/bin/docker.exe')) {
            if (Test-Path -LiteralPath $candidate) { $docker = Get-Command $candidate; break }
        }
    }
    if (-not $docker) { throw 'Docker Desktop CLI is required.' }
    return $docker
}
function Invoke-LocalDocker($Docker, [string[]]$Arguments) {
    $output = & $Docker.Source @Arguments 2>&1
    # Native output can contain connection details; never include it in errors.
    if ($LASTEXITCODE -ne 0) { throw 'Docker operation failed. Installation state is preserved; inspect the current stage before retrying.' }
    return $output
}
function New-SetupMount([string]$Source, [string]$Target, [bool]$ReadOnly = $true, [string]$Type = 'bind') {
    $mount = @{type=$Type;source=$Source;target=$Target;read_only=$ReadOnly}
    if ($Type -eq 'bind') { $mount.bind = @{create_host_path=$false} }
    return $mount
}
function Protect-LocalDirectory([string]$Path) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls $Path /inheritance:r /grant:r "*${sid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Installation ACL could not be secured. No secrets generated.' }
}
function Invoke-DatabaseContainer($Context, [string]$Role, [string[]]$Command, [string[]]$AdditionalSecrets = @(), [string[]]$ContainerArguments = @()) {
    $root = $Context.Root
    $network = if ($Context.Network) { $Context.Network } else { "$($Context.Project)_dependencies" }
    $argsList = @('run','--rm','--pull','never','--network',$network,'--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true',
        '--tmpfs','/tmp:rw,noexec,nosuid,size=64m,uid=1654,gid=1654')
    if ($Context.TrustBundle) {
        $argsList += @('--env','SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt',
            '--mount',"type=bind,source=$($Context.TrustBundle),target=/etc/ssl/certs/ca-certificates.crt,readonly")
    }
    $argsList += @('--mount',"type=bind,source=$root/secrets/$Role-connection,target=/run/secrets/connection,readonly")
    foreach ($secret in $AdditionalSecrets) { $argsList += @('--mount',"type=bind,source=$root/secrets/$secret,target=/run/secrets/$secret,readonly") }
    $argsList += $ContainerArguments
    $argsList += @('--entrypoint','dotnet',$Context.Image,'/opt/workbench/database/Workbench.Database.dll') + $Command + @('--connection-file','/run/secrets/connection','--expected-database','Workbench')
    return Invoke-LocalDocker $Context.Docker $argsList
}
function New-WorkbenchSqlConnection([string]$Server, [string]$Database, [string]$User, [string]$Password, [bool]$TrustServerCertificate = $false, [int]$MaxPoolSize = 5) {
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Server'] = $Server; $builder['Database'] = $Database
    $builder['User ID'] = $User; $builder['Password'] = $Password
    $builder['Encrypt'] = $true; $builder['TrustServerCertificate'] = $TrustServerCertificate
    $builder['Persist Security Info'] = $false; $builder['Connect Timeout'] = 15
    $builder['Max Pool Size'] = $MaxPoolSize
    return $builder.ConnectionString
}
