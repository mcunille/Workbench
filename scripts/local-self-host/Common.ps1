# Copyright (c) 2026 The White Stag Collection.
# Shared local self-host primitives. Deployment state is supplied explicitly.
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
function Assert-LocalEnvironment {
    if (Get-ChildItem Env: | Where-Object Name -Match '^(COMPOSE_|WORKBENCH_|ConnectionStrings__|Storage__|DataProtection__)') { throw 'Run setup or update in a clean shell without deployment overrides.' }
}
function Resolve-LocalRevision([string]$Repository, [string]$SourceRef) {
    $revision = & git -C $Repository rev-parse --verify "$SourceRef^{commit}"
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40}$') { throw 'SourceRef must resolve to a committed revision.' }
    return $revision
}
function Build-LocalImage($Docker, [string]$Repository, [string]$Revision, [string]$Directory) {
    New-Item -ItemType Directory -Path "$Directory/source" -Force | Out-Null
    & git -C $Repository archive --format=tar "--output=$Directory/source.tar" $Revision
    if ($LASTEXITCODE -ne 0) { throw 'Git archive failed.' }
    & tar -xf "$Directory/source.tar" -C "$Directory/source"
    if ($LASTEXITCODE -ne 0) { throw 'Source archive extraction failed.' }
    Invoke-LocalDocker $Docker @('build','--pull','--label',"org.opencontainers.image.revision=$Revision",'--iidfile',"$Directory/image-id", "$Directory/source") | Out-Null
    $image = (Get-Content -LiteralPath "$Directory/image-id" -Raw).Trim()
    if ($image -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Build did not produce an immutable image ID.' }
    return $image
}
function New-SetupMount([string]$Source, [string]$Target, [bool]$ReadOnly = $true, [string]$Type = 'bind') {
    $mount = @{type=$Type;source=$Source;target=$Target;read_only=$ReadOnly}
    if ($Type -eq 'bind') { $mount.bind = @{create_host_path=$false} }
    return $mount
}
function Invoke-LocalDatabase($Context, [string]$Role, [string[]]$Command, [string[]]$AdditionalSecrets = @(), [string[]]$MountArguments = @()) {
    $root = $Context.Root
    $project = $Context.Project
    $argsList = @('run','--rm','--pull','never','--network',"${project}_dependencies",'--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true',
        '--tmpfs','/tmp:rw,noexec,nosuid,size=64m,uid=1654,gid=1654','--env','SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt',
        '--mount',"type=bind,source=$root/trust/ca-certificates.crt,target=/etc/ssl/certs/ca-certificates.crt,readonly",
        '--mount',"type=bind,source=$root/secrets/$Role-connection,target=/run/secrets/connection,readonly")
    foreach ($secret in $AdditionalSecrets) { $argsList += @('--mount',"type=bind,source=$root/secrets/$secret,target=/run/secrets/$secret,readonly") }
    $argsList += $MountArguments
    $argsList += @('--entrypoint','dotnet',$Context.Image,'/opt/workbench/database/Workbench.Database.dll') + $Command + @('--connection-file','/run/secrets/connection','--expected-database','Workbench')
    Invoke-LocalDocker $Context.Docker $argsList | Out-Null
}
function Invoke-LocalSql($Context, [string]$Sql) {
    $docker = $Context.Docker
    $compose = $Context.Compose
    $command = 'export SQLCMDPASSWORD="$(cat /run/secrets/sql-bootstrap-password)"; exec /opt/mssql-tools18/bin/sqlcmd -S tcp:sql,1433 -U sa -d master -N -b -l 5 -t 60'
    $Sql | & $docker.Source @compose exec -T sql /bin/bash -ec $command *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Validated SQL operation failed.' }
}
function Wait-LocalApp($Context) {
    $docker = $Context.Docker
    $compose = $Context.Compose
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do {
        & $docker.Source @compose exec -T app dotnet Workbench.Server.dll --health-check *> $null
        $ready = $LASTEXITCODE -eq 0
        if (-not $ready) { Start-Sleep -Seconds 2 }
    } while (-not $ready -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'Application readiness failed.' }
}
function Protect-LocalDirectory([string]$Path) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls $Path /inheritance:r /grant:r "*${sid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Installation ACL could not be secured. No secrets generated.' }
}
