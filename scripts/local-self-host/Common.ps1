. "$PSScriptRoot/../lib/LocalRuntime.ps1"
# Copyright (c) 2026 The White Stag Collection.
# Shared local self-host primitives. Deployment state is supplied explicitly.


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

function Invoke-LocalDatabase($Context, [string]$Role, [string[]]$Command, [string[]]$AdditionalSecrets = @(), [string[]]$MountArguments = @()) {
    $databaseContext = @{
        Docker=$Context.Docker; Root=$Context.Root; Project=$Context.Project; Image=$Context.Image
        TrustBundle="$($Context.Root)/trust/ca-certificates.crt"
    }
    Invoke-DatabaseContainer $databaseContext $Role $Command $AdditionalSecrets $MountArguments | Out-Null
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
