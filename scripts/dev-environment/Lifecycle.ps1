. "$PSScriptRoot/../lib/LocalRuntime.ps1"
. "$PSScriptRoot/../local-self-host/Configuration.ps1"
foreach ($file in @('State','Source','Compose','Resources','Database')) { . "$PSScriptRoot/$file.ps1" }

function Get-DevStatus($Context, [switch]$Candidate) {
    Assert-DevResources $Context
    $app = Get-DevResource $Context container app
    $sql = Get-DevResource $Context container sql
    $url = $null; $ready = $false
    if ($app -and $app.State.Running -and $app.Image -ceq $Context.State.Image) {
        $binding = @($app.NetworkSettings.Ports.'8080/tcp')
        if ($binding.Count -ne 1 -or $binding[0].HostIp -cne '127.0.0.1') { throw 'Unexpected application listener; refusing to advertise this preview.' }
        $url = "http://localhost:$($binding[0].HostPort)"
        if ($sql -and $sql.State.Running) {
            try {
                $health = Invoke-WebRequest "$url/health/ready" -TimeoutSec 5 -SkipHttpErrorCheck
                $ui = Invoke-WebRequest $url -TimeoutSec 5 -SkipHttpErrorCheck
                $ready = $health.StatusCode -eq 200 -and $ui.StatusCode -eq 200 -and $ui.Content.Contains('<div id="root">')
            } catch { $ready = $false }
        }
    }
    if (-not $Candidate -and $app -and $app.Image -cne $Context.State.Build.Image) { $ready = $false }
    $sourceChanged = $null; $sourceError = $null
    try { $sourceChanged = $Context.State.Build.SourceHash -cne (Get-DevSource $Context.Owner.Root).Hash }
    catch { $sourceError = 'Build inputs are unavailable; restore valid source before refreshing the preview.' }
    [ordered]@{ Version=1; EnvironmentId=$Context.State.EnvironmentId; Checkout=$Context.Owner.Root
        Phase=$Context.State.Phase; Ready=$ready; Url=$url; Build=$Context.State.Build
        SourceChanged=$sourceChanged; SourceError=$sourceError; AdminEmail=$Context.State.AdminEmail
        LoginFile="$($Context.Root)/secrets/login.txt" }
}
function Stop-DevEnvironment($Context) {
    Assert-DevResources $Context
    Set-DevPhase $Context 'stopping'
    foreach ($key in @('tool','app','sql')) {
        $container = Get-DevResource $Context container $key
        if ($container -and $container.State.Running) { Invoke-DevDocker $Context @('container','stop','--time','30',$container.Id) | Out-Null }
    }
    Set-DevPhase $Context 'stopped'
}
function Start-DevEnvironment($Context) {
    $wasRunning = @{}
    $originalIds = @{}
    foreach ($key in @('app','sql','tool')) {
        $resource = Get-DevResource $Context container $key
        $wasRunning[$key] = $resource -and $resource.State.Running
        $originalIds[$key] = $resource.Id
    }
    try { Start-DevEnvironmentCore $Context }
    catch {
        $failure = $_
        $pending = @()
        foreach ($key in @('tool','app','sql')) {
            try {
                $resource = Get-DevResource $Context container $key
                if ($resource -and $resource.State.Running -and (-not $wasRunning[$key] -or $resource.Id -cne $originalIds[$key])) {
                    Invoke-DevDocker $Context @('container','stop','--time','30',$resource.Id) | Out-Null
                }
            } catch { $pending += $key }
        }
        $Context.State.CleanupPending = $pending
        Write-DevState $Context
        throw $failure
    }
}
function Start-DevEnvironmentCore($Context) {
    Assert-DevResources $Context
    $source = Get-DevSource $Context.Owner.Root
    if ($Context.State.Build.SourceHash -ceq $source.Hash -and $Context.State.Image -ceq $Context.State.Build.Image -and (Get-DevStatus $Context).Ready) { Set-DevPhase $Context 'ready'; return }
    # A source match is reusable only while its immutable image remains in Docker's local cache.
    # Listing full IDs includes untagged builds and lets engine failures propagate before refresh.
    $image = if ($Context.State.Build.SourceHash -ceq $source.Hash -and $Context.State.Build.Image -cin @(Invoke-DevDocker $Context @('image','ls','--all','--no-trunc','--quiet'))) { $Context.State.Build.Image } else { Build-DevImage $Context $source }
    # No mutation of a running preview until the source build has succeeded.
    Set-DevPhase $Context 'refresh'
    Remove-DevContainer $Context app
    $Context.State.Image = $image
    Write-DevState $Context
    Initialize-DevSecrets $Context
    Initialize-DevResources $Context
    Write-DevCompose $Context
    Invoke-DevCompose $Context @('up','-d','--no-deps','sql') | Out-Null
    Register-DevResource $Context container sql | Out-Null
    Initialize-DevDatabase $Context
    Set-DevPhase $Context 'app-start'
    try { Invoke-DevCompose $Context @('up','-d','--no-deps','app') | Out-Null }
    catch {
        # Docker performs actual binding. Retry once with dynamic allocation after a preferred port collision.
        if (-not $Context.State.Port) { throw }
        Remove-DevContainer $Context app
        $Context.State.Port = 0; Write-DevState $Context; Write-DevCompose $Context
        Invoke-DevCompose $Context @('up','-d','--no-deps','app') | Out-Null
    }
    $app = Register-DevResource $Context container app
    $Context.State.Port = [int]$app.NetworkSettings.Ports.'8080/tcp'[0].HostPort
    Write-DevState $Context
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do {
        if ((Get-DevStatus $Context -Candidate).Ready) { $ready = $true; break }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'Application/UI readiness failed within two minutes.' }
    if ((Get-DevSource $Context.Owner.Root).Hash -cne $source.Hash) { throw 'Source changed during startup. Preview is stale; rerun dev-up.' }
    $revision = (& git -C $Context.Owner.Root rev-parse HEAD 2>$null)
    $branch = (& git -C $Context.Owner.Root branch --show-current 2>$null)
    $dirty = [bool](& git -C $Context.Owner.Root status --porcelain 2>$null)
    $Context.State.Build = @{ SourceHash=$source.Hash; Image=$image; Revision=$revision; Branch=$branch; Dirty=$dirty; BuiltAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    Set-DevPhase $Context 'ready'
}
function Destroy-DevEnvironment($Context, [string]$EnvironmentId) {
    if ($EnvironmentId -cne $Context.State.EnvironmentId) { throw 'Destroy requires the exact EnvironmentId reported by dev-status.' }
    Assert-DevResources $Context
    Set-DevPhase $Context 'destroying'
    foreach ($key in @('tool','app','sql')) { Remove-DevContainer $Context $key }
    foreach ($entry in @(@('volume','blobs'),@('volume','sql-data'),@('network','network'))) {
        $kind,$key = $entry
        $resource = Get-DevResource $Context $kind $key
        if ($resource) { Invoke-DevDocker $Context @($kind,'rm',(Get-DevResourceName $Context $key)) | Out-Null }
        $Context.State.Resources.Remove($key); Write-DevState $Context
    }
    # Preserve the lock and a non-secret tombstone so concurrent callers cannot acquire different lock files.
    foreach ($name in @('secrets','compose.json')) {
        $path = [IO.Path]::GetFullPath((Join-Path $Context.Root $name))
        if ([IO.Path]::GetDirectoryName($path) -cne [IO.Path]::GetFullPath($Context.Root)) { throw 'Unexpected cleanup path.' }
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Set-DevPhase $Context 'destroyed'
}
function Invoke-DevCommand([string]$Action, [string]$Repository, [string]$TenantName, [string]$AdminEmail, [string]$EnvironmentId, [switch]$Json) {
    $ErrorActionPreference = 'Stop'
    $PSNativeCommandUseErrorActionPreference = $false
    $lock = $null; $context = $null; $operationStarted = $false
    try {
        if (-not $IsWindows) { throw 'Worktree previews currently require Windows, PowerShell 7, and Docker Desktop Linux containers.' }
        $owner = Get-DevOwner $Repository
        $root = Join-Path $owner.Root '.dev-environment'
        $state = Read-DevState $root $owner
        if (-not $state -and $Action -ne 'up') {
            $result = @{Version=1;Phase='absent';Ready=$false;Checkout=$owner.Root}
        } else {
            if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root -Force | Out-Null }
            if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Environment state directory cannot be a reparse point.' }
            if ($Action -ne 'status') {
                Protect-LocalDirectory $root
                $lock = Enter-DevLock $root
                $state = Read-DevState $root $owner
            }
            if ($Action -eq 'up' -and (-not $state -or $state.Phase -eq 'destroyed')) {
                $settings = Get-LocalSetupConfiguration @{TenantName=$TenantName;AdminEmail=$AdminEmail;InstallationRoot=$root}
                $state = @{Version=1;EnvironmentId=('dev-'+[Guid]::NewGuid().ToString('N'));Owner=$owner;Resources=@{};Port=0
                    Phase='prepare';InstallationId=[Guid]::NewGuid().ToString();TenantName=$settings.TenantName;AdminEmail=$settings.AdminEmail;Build=@{} }
            }
            $docker = Get-LocalDocker
            Invoke-LocalDocker $docker @('version') | Out-Null
            Invoke-LocalDocker $docker @('compose','version') | Out-Null
            if ((Invoke-LocalDocker $docker @('info','--format','{{.OSType}}')) -cne 'linux') { throw 'Docker engine must use Linux containers.' }
            # Compose override variables are never allowed to alter environment authority.
            if (Get-ChildItem Env: | Where-Object Name -Like 'COMPOSE_*') { throw 'Run preview commands without COMPOSE_* overrides.' }
            $context = @{Owner=$owner;Root=$root;State=$state;Docker=$docker}
            if ($Action -eq 'destroy' -and $EnvironmentId -cne $state.EnvironmentId) { throw 'Destroy requires the exact EnvironmentId reported by dev-status.' }
            Assert-DevResources $context
            if ($Action -ne 'status') { $operationStarted = $true; Write-DevState $context }
            if ($state.Phase -ne 'destroyed') {
                switch ($Action) {
                    up { Start-DevEnvironment $context }
                    down { Stop-DevEnvironment $context }
                    destroy { Destroy-DevEnvironment $context $EnvironmentId }
                }
            }
            $result = Get-DevStatus $context
        }
        if ($Json) { $result | ConvertTo-Json -Depth 10 -Compress }
        else {
            Write-Host "Workbench preview: $($result.Phase)"
            if ($result.EnvironmentId) { Write-Host "Environment: $($result.EnvironmentId)" }
            Write-Host "Checkout: $($result.Checkout)"
            if ($result.Url) { Write-Host "URL: $($result.Url)" }
            if ($result.Build) { Write-Host "Revision: $($result.Build.Revision) ($($result.Build.Branch)), dirty=$($result.Build.Dirty), source changed=$($result.SourceChanged)" }
            if ($result.Ready) { Write-Host "Login: $($result.AdminEmail); password file: $($result.LoginFile)" }
            Write-Host 'Status: ./scripts/dev-status.ps1; stop: ./scripts/dev-down.ps1'
        }
        if ($Action -in @('up','status') -and -not $result.Ready) { return 1 }
        return 0
    } catch {
        $message = $_.Exception.Message
        if ($context -and $operationStarted) {
            $context.State.FailedPhase = $context.State.Phase
            try { Set-DevPhase $context 'failed' } catch { }
        }
        if ($Json) { @{Version=1;Ready=$false;Phase='failed';FailedPhase=$context.State.FailedPhase;Error=$message} | ConvertTo-Json -Compress }
        else { Write-Host "Preview failed: $message" }
        return 1
    } finally { if ($lock) { $lock.Dispose() } }
}
