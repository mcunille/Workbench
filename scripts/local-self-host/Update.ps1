# Copyright (c) 2026 The White Stag Collection.
function Write-UpdateJson($Value, [string]$Path) {
    $temporary = "$Path.new"
    [IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $Value -Depth 30))
    [IO.File]::Move($temporary, $Path, $true)
}
function Write-UpdateJournal($Context, [string]$Phase, [string]$Status = 'Running') {
    $Context.Phase = $Phase
    Write-UpdateJson @{
        Version=1; Phase=$Phase; Status=$Status; UpdatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        PreviousRevision=$Context.PreviousRevision; SourceRevision=$Context.Revision
        PreviousImage=$Context.PreviousImage; Image=$Context.CandidateImage
        Release=$Context.Release; Checkpoint=$Context.Checkpoint; Project=$Context.Project
        FailedPhase=$Context.FailedPhase; WorkloadsVerifiedStopped=$Context.Offline
        SnapshotVolume=$Context.SnapshotVolume
    } "$($Context.Root)/update.json"
}
function Assert-UpdateHttps($Context) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        try {
            $response = Invoke-WebRequest "$($Context.Origin)/health/ready" -TimeoutSec 10 -MaximumRedirection 0
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'Trusted HTTPS readiness failed.'
}
function Get-UpdateContainers($Context) {
    $ids = @(Invoke-LocalDocker $Context.Docker @('container','ls','-a','--filter',"label=com.docker.compose.project=$($Context.Project)",'--format','{{.ID}}'))
    if (-not $ids.Count) { throw 'Installation containers are missing.' }
    return @(Invoke-LocalDocker $Context.Docker (@('container','inspect') + $ids) | Out-String | ConvertFrom-Json)
}
function Assert-UpdateOffline($Context) {
    $containers = @(Get-UpdateContainers $Context)
    foreach ($container in $containers) {
        if ($container.Config.Labels.'com.docker.compose.service' -ne 'sql' -and $container.State.Running) { throw 'Installation writer or ingress is still running.' }
    }
    foreach ($network in @('dependencies','ingress')) {
        $detail = (Invoke-LocalDocker $Context.Docker @('network','inspect',"$($Context.Project)_$network") | Out-String | ConvertFrom-Json)[0]
        foreach ($id in $detail.Containers.PSObject.Properties.Name) {
            if ($id -notin $containers.Id) { throw 'An unrecognized container is attached to the installation network.' }
        }
    }
}
function Assert-UpdateRunning($Context) {
    $containers = @(Get-UpdateContainers $Context)
    foreach ($service in @('sql','app','worker','proxy')) {
        $matching = @($containers | Where-Object { $_.Config.Labels.'com.docker.compose.service' -eq $service })
        if ($matching.Count -ne 1 -or -not $matching[0].State.Running -or $matching[0].State.Restarting) { throw "Service $service is not running stably." }
        if ($service -in @('app','worker') -and $matching[0].Image -ne $Context.Image) { throw 'Runtime image does not match the selected release.' }
    }
    if ($containers.Count -ne 4) { throw 'Unexpected installation containers; stop external jobs before updating.' }
}
function Assert-UpdateConfigurationCurrent($Context) {
    # Compose's canonical service hash includes mounts, networks, image and execution
    # settings. A changed file must not describe a different service from the one backed up.
    $lines = @(Invoke-LocalDocker $Context.Docker ($Context.Compose + @('config','--hash','*')))
    $hashes = @{}
    foreach ($line in $lines) {
        if ($line -notmatch '^(app|worker|sql|proxy) ([a-f0-9]{64})$') { throw 'Could not verify canonical Compose service hashes.' }
        $hashes[$Matches[1]]=$Matches[2]
    }
    if ($hashes.Count -ne 4) { throw 'Compose did not identify all installed services.' }
    foreach ($container in @(Get-UpdateContainers $Context)) {
        $service=$container.Config.Labels.'com.docker.compose.service'
        if (-not $hashes.ContainsKey($service) -or $container.Config.Labels.'com.docker.compose.config-hash' -ne $hashes[$service]) { throw 'Compose configuration differs from installed containers; reconcile it before updating.' }
    }
}
function Assert-UpdateInstallation($Context) {
    $root = $Context.Root
    $state = Get-Content -LiteralPath "$root/installation.json" -Raw | ConvertFrom-Json -AsHashtable
    $config = Get-Content -LiteralPath $Context.ComposeFile -Raw | ConvertFrom-Json -AsHashtable
    $project = 'workbench-local-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant()))).Substring(0,10).ToLowerInvariant()
    if ($state.Status -notin @('ReadyForBrowserVerification','AwaitingWindowsTrust') -or $state.Project -ne $project -or $config.name -ne $project) { throw 'Expected a completed setup installation at its original path.' }
    if (Test-Path "$root/update.json") {
        $previous = Get-Content "$root/update.json" -Raw | ConvertFrom-Json -AsHashtable
        if ($previous.Version -ne 1 -or $previous.Status -ne 'Succeeded') { throw 'An incomplete update requires recovery; inspect update.json before retrying.' }
        if ($previous.Project -ne $project -or $previous.Image -ne $config.services.app.image) { throw 'Previous update evidence does not match this installation.' }
        $Context.PreviousRevision = $previous.SourceRevision
    } else { $Context.PreviousRevision = $state.SourceRevision }
    if ($Context.PreviousRevision -notmatch '^[a-f0-9]{40}$') { throw 'Previous source revision is missing.' }
    if (($config.Keys | Where-Object { $_ -notin @('name','services','networks','volumes') }) -or $config.services.Count -ne 4) { throw 'Customized Compose structure is unsupported.' }
    foreach ($service in @('app','worker','sql','proxy')) { if (-not $config.services.ContainsKey($service)) { throw 'Expected setup services are missing.' } }
    $image = $config.services.app.image
    if ($image -notmatch '^sha256:[a-f0-9]{64}$' -or $config.services.worker.image -ne $image) { throw 'App and worker must share the installed immutable image.' }
    $origin = [uri]$state.PublicOrigin
    if ($origin.Scheme -ne 'https' -or $origin.Host -ne 'localhost' -or $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.UserInfo -or $origin.Fragment) { throw 'Only localhost HTTPS installations are supported.' }
    foreach ($role in @('app','worker')) {
        $service = $config.services[$role]
        $credential = if ($role -eq 'app') { 'web' } else { 'worker' }
        $connectionKey = if ($role -eq 'app') { 'ConnectionStrings__WorkbenchFile' } else { 'ConnectionStrings__WorkerFile' }
        $expectedNetworks = if ($role -eq 'app') { @('dependencies','ingress') } else { @('dependencies') }
        if (($service.networks -join ',') -ne ($expectedNetworks -join ',') -or $service.environment[$connectionKey] -ne "/run/secrets/$credential-connection") { throw 'Customized runtime network or credential file is unsupported.' }
        if ($service.entrypoint -or $service.ports -or $service.build -or $service.env_file -or $service.secrets -or $service.privileged -or $service.user -ne '1654:1654' -or -not $service.read_only -or $service.pull_policy -ne 'never') { throw 'Customized runtime execution is unsupported.' }
        if (($role -eq 'app' -and $service.command) -or ($role -eq 'worker' -and ($service.command -join ' ') -ne '--worker')) { throw 'Customized runtime command is unsupported.' }
        if ($service.environment.Storage__Provider -ne 'FileSystem' -or $service.environment.Storage__Root -ne '/var/lib/workbench/blobs' -or $service.environment.PublicOrigin -ne $state.PublicOrigin) { throw 'Unsupported storage or origin configuration.' }
        $expectedMounts = @{'/var/lib/workbench/blobs'='blobs'; '/etc/ssl/certs/ca-certificates.crt'="$root/trust/ca-certificates.crt"}
        foreach ($name in @("$credential-connection",'tenant-proof','data-protection.pfx','certificate-password')) { $expectedMounts["/run/secrets/$name"] = "$root/secrets/$name" }
        if ($service.volumes.Count -ne $expectedMounts.Count) { throw 'Customized runtime mounts are unsupported.' }
        if (@($service.volumes.target | Select-Object -Unique).Count -ne $expectedMounts.Count) { throw 'Duplicate runtime mount targets are unsupported.' }
        foreach ($mount in $service.volumes) {
            if ($mount.volume) { throw 'Customized volume options are unsupported.' }
            if (-not $expectedMounts.ContainsKey($mount.target) -or $mount.source -ne $expectedMounts[$mount.target]) { throw 'Runtime mounts do not match the installation root.' }
            if ($mount.target -eq '/var/lib/workbench/blobs') {
                if ($mount.type -ne 'volume' -or $mount.read_only) { throw 'Unsupported blob volume.' }
            } elseif ($mount.type -ne 'bind' -or -not $mount.read_only -or $mount.bind.create_host_path) { throw 'Unsupported runtime secret mount.' }
        }
        foreach ($key in $service.environment.Keys) {
            if ($key -match '^(Storage__|ConnectionStrings__|DataProtection__)' -and $key -notin @('Storage__Provider','Storage__Root','Storage__DurableVolume','Storage__InstallationId','ConnectionStrings__WorkbenchFile','ConnectionStrings__WorkerFile','DataProtection__CertificatePasswordFile')) { throw 'Customized storage, credentials, or certificate rotation requires a reviewed update.' }
        }
    }
    $installationId = [guid]::Parse($config.services.app.environment.Storage__InstallationId)
    if ($installationId -eq [guid]::Empty -or $config.services.worker.environment.Storage__InstallationId -ne $installationId.ToString()) { throw 'Installation identity mismatch.' }
    if ($config.services.sql.ports -or $config.services.proxy.ports.Count -ne 2 -or ($config.services.proxy.ports | Where-Object { $_ -notmatch '^127\.0\.0\.1:[0-9]+:(80|443)$' })) { throw 'Installation listeners must remain private or loopback-only.' }
    foreach ($dependency in @('sql','proxy')) { if ($config.services[$dependency].image -notmatch '@sha256:[a-f0-9]{64}$') { throw 'Dependency images must remain pinned.' } }
    $dependencyMounts = @{
        sql=@{'/var/opt/mssql'=@('sql-data','volume',$false);'/var/opt/mssql/tls'=@('sql-tls','volume',$true);'/var/opt/mssql/mssql.conf'=@("$root/source/infra/compose/mssql.conf",'bind',$true);'/run/secrets/sql-bootstrap-password'=@("$root/secrets/sql-bootstrap-password",'bind',$true);'/etc/ssl/certs/ca-certificates.crt'=@("$root/trust/ca-certificates.crt",'bind',$true)}
        proxy=@{'/etc/caddy/Caddyfile'=@("$root/Caddyfile",'bind',$true);'/data'=@('proxy-data','volume',$false);'/config'=@('proxy-config','volume',$false)}
    }
    foreach ($role in @('sql','proxy')) {
        $expected = $dependencyMounts[$role]
        $mounts = $config.services[$role].volumes
        if ($mounts.Count -ne $expected.Count -or @($mounts.target | Select-Object -Unique).Count -ne $expected.Count) { throw 'Customized dependency mounts are unsupported.' }
        foreach ($mount in $mounts) {
            if ($mount.volume) { throw 'Customized volume options are unsupported.' }
            $entry = $expected[$mount.target]
            if (-not $entry -or $mount.source -ne $entry[0] -or $mount.type -ne $entry[1] -or $mount.read_only -ne $entry[2] -or $mount.bind.create_host_path) { throw 'Dependency mount differs from generated installation.' }
        }
    }
    if ($config.volumes.Count -ne 5 -or $config.networks.Count -ne 2) { throw 'Customized volumes or networks are unsupported.' }
    foreach ($name in @('sql-data','blobs','proxy-data','proxy-config','sql-tls')) {
        if (-not $config.volumes.ContainsKey($name)) { throw 'Required retained volume missing.' }
        $definition = $config.volumes[$name]
        if ($name -eq 'sql-tls') {
            if (-not $definition.external -or $definition.name -ne "${project}_sql-tls") { throw 'SQL TLS volume mismatch.' }
        } elseif ($definition.Count) { throw 'Custom volume mapping is unsupported.' }
        $volume = (Invoke-LocalDocker $Context.Docker @('volume','inspect',"${project}_$name") | Out-String | ConvertFrom-Json)[0]
        if ($volume.Name -ne "${project}_$name" -or ($name -ne 'sql-tls' -and $volume.Labels.'com.docker.compose.project' -ne $project)) { throw 'Retained volume ownership mismatch.' }
    }
    foreach ($file in @('secrets/migrator-connection','secrets/maintenance-connection','secrets/sql-bootstrap-password','trust/ca-certificates.crt','trust/caddy-local-root.crt','Caddyfile','source/infra/compose/mssql.conf')) {
        if (-not (Test-Path -LiteralPath "$root/$file" -PathType Leaf)) { throw "Required installation file missing: $file" }
    }
    # Backup, snapshot and migration must target the same generated database, with
    # separate least-privilege identities. Do not echo parser exceptions or values.
    foreach ($role in @('web','worker','maintenance','migrator')) {
        $valid=$false
        try {
            $connection=[System.Data.Common.DbConnectionStringBuilder]::new()
            $connection.set_ConnectionString([IO.File]::ReadAllText("$root/secrets/$role-connection"))
            $valid=$connection['Server'] -eq 'tcp:sql,1433' -and $connection['Database'] -eq 'Workbench' -and $connection['User ID'] -eq "workbench_${role}_local" -and $connection['Encrypt'] -eq 'True' -and $connection['TrustServerCertificate'] -eq 'False' -and -not [string]::IsNullOrWhiteSpace($connection['Password'])
            # Reject aliases/extra connection options that could override identity or routing.
            if (@($connection.Keys | Where-Object { $_ -notin @('Server','Database','User ID','Password','Encrypt','TrustServerCertificate','Persist Security Info','Connect Timeout','Max Pool Size') }).Count) { $valid=$false }
        } catch { $valid=$false } finally { $connection=$null }
        if (-not $valid) { throw "The $role connection does not match the generated local SQL identity and validated TLS contract." }
    }
    $Context.Project=$project; $Context.Config=$config; $Context.Image=$image; $Context.PreviousImage=$image
    $Context.Origin=$state.PublicOrigin; $Context.InstallationId=$installationId.ToString()
    $Context.Compose=@('compose','--project-name',$project,'--file',$Context.ComposeFile)
    Invoke-LocalDocker $Context.Docker ($Context.Compose + @('config','--quiet')) | Out-Null
    Assert-UpdateRunning $Context
    Assert-UpdateConfigurationCurrent $Context
    # Check foreign network attachments before downtime as well as after draining.
    $containers = @(Get-UpdateContainers $Context)
    foreach ($network in @('dependencies','ingress')) {
        $detail = (Invoke-LocalDocker $Context.Docker @('network','inspect',"${project}_$network") | Out-String | ConvertFrom-Json)[0]
        if ($detail.Labels.'com.docker.compose.project' -ne $project) { throw 'Network ownership mismatch.' }
        foreach ($id in $detail.Containers.PSObject.Properties.Name) { if ($id -notin $containers.Id) { throw 'Unrecognized container on installation network.' } }
    }
    Assert-UpdateHttps $Context
}
function New-UpdateCheckpoint($Context) {
    $checkpoint = $Context.Checkpoint
    New-Item -ItemType Directory $checkpoint | Out-Null
    Protect-LocalDirectory $checkpoint
    New-Item -ItemType Directory "$checkpoint/blobs" | Out-Null
    $backupName = 'update-' + [Guid]::NewGuid().ToString('N') + '.bak'
    $serverPath = "/var/opt/mssql/$backupName"
    Invoke-LocalSql $Context "BACKUP DATABASE [Workbench] TO DISK=N'$serverPath' WITH COPY_ONLY, CHECKSUM; RESTORE VERIFYONLY FROM DISK=N'$serverPath' WITH CHECKSUM;"
    $sql = (Invoke-LocalDocker $Context.Docker ($Context.Compose + @('ps','-q','sql')) | Out-String).Trim()
    if (-not $sql) { throw 'SQL container missing during checkpoint.' }
    Invoke-LocalDocker $Context.Docker @('cp',"${sql}:$serverPath","$checkpoint/database.bak") | Out-Null
    if ((Get-Item "$checkpoint/database.bak").Length -eq 0) { throw 'SQL backup copy is empty.' }
    $storage = @{Provider='FileSystem';Root='/var/lib/workbench/blobs';DurableVolume=$true;InstallationId=$Context.InstallationId}
    $target = $storage.Clone(); $target.Root='/backup/blobs'
    Write-UpdateJson @{Storage=$storage;Target=@{Storage=$target}} "$checkpoint/storage.json"
    # Windows bind mounts do not implement renameat2(RENAME_NOREPLACE). Snapshot
    # on the Linux engine, then verify the exported Windows copy independently.
    $Context.SnapshotVolume = "$($Context.Project)_checkpoint-$([Guid]::NewGuid().ToString('N'))"
    Write-UpdateJournal $Context 'checkpoint'
    if (Invoke-LocalDocker $Context.Docker @('volume','ls','--filter',"name=^$($Context.SnapshotVolume)`$",'--format','{{.Name}}')) { throw 'Checkpoint volume already exists.' }
    Invoke-LocalDocker $Context.Docker @('volume','create',$Context.SnapshotVolume) | Out-Null
    Invoke-LocalDocker $Context.Docker @('run','--rm','--network','none','--user','0:0','--read-only','--cap-drop','ALL','--cap-add','CHOWN','--security-opt','no-new-privileges:true','--mount',"type=volume,source=$($Context.SnapshotVolume),target=/snapshot",'--entrypoint','/bin/bash',$Context.Config.services.sql.image,'-ec','mkdir -p /snapshot/blobs; chown -R 1654:1654 /snapshot') | Out-Null
    Invoke-LocalDatabase $Context 'maintenance' @('storage','snapshot','--offline-confirmation','OFFLINE Workbench','--config-file','/run/checkpoint-storage.json','--output-file','/backup/manifest.json') @() @('--mount',"type=volume,source=$($Context.Project)_blobs,target=/var/lib/workbench/blobs,readonly",'--mount',"type=volume,source=$($Context.SnapshotVolume),target=/backup",'--mount',"type=bind,source=$checkpoint/storage.json,target=/run/checkpoint-storage.json,readonly")
    Invoke-LocalDocker $Context.Docker @('run','--rm','--network','none','--user','0:0','--read-only','--cap-drop','ALL','--cap-add','DAC_READ_SEARCH','--security-opt','no-new-privileges:true','--mount',"type=volume,source=$($Context.SnapshotVolume),target=/snapshot,readonly",'--mount',"type=bind,source=$checkpoint,target=/backup",'--entrypoint','/bin/bash',$Context.Config.services.sql.image,'-ec','cp -R --no-preserve=mode,ownership /snapshot/. /backup/') | Out-Null
    $manifest = Get-Content "$checkpoint/manifest.json" -Raw | ConvertFrom-Json
    if ($manifest.Version -ne 1 -or $null -eq $manifest.Entries -or [guid]$manifest.BackupId -eq [guid]::Empty -or $manifest.InstallationId -ne $Context.InstallationId -or $manifest.Database -ne 'Workbench' -or -not $manifest.SchemaVersion) { throw 'Snapshot manifest identity mismatch.' }
    foreach ($entry in $manifest.Entries) {
        $name=([guid]$entry.TenantId).ToString('N') + '-' + ([guid]$entry.RevisionId).ToString('N') + '.b'
        $file=Get-Item -LiteralPath "$checkpoint/blobs/$name"
        if ($entry.Length -lt 0 -or $entry.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $file.Length -ne $entry.Length -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $entry.Sha256) { throw 'Exported checkpoint blob does not match the snapshot manifest.' }
    }
    foreach ($directory in @('secrets','trust')) { Copy-Item -LiteralPath "$($Context.Root)/$directory" -Destination "$checkpoint/$directory" -Recurse }
    foreach ($file in @('compose.json','installation.json','Caddyfile')) { Copy-Item -LiteralPath "$($Context.Root)/$file" -Destination "$checkpoint/$file" }
    Copy-Item -LiteralPath "$($Context.Root)/source/infra/compose/mssql.conf" -Destination "$checkpoint/mssql.conf"
    # Preserve Caddy's private CA/renewal state as well as its exported public certificate.
    foreach ($volume in @('proxy-data','proxy-config')) {
        Invoke-LocalDocker $Context.Docker @('run','--rm','--network','none','--user','0:0','--read-only','--cap-drop','ALL','--cap-add','DAC_READ_SEARCH','--security-opt','no-new-privileges:true','--mount',"type=volume,source=$($Context.Project)_$volume,target=/source,readonly",'--mount',"type=bind,source=$checkpoint,target=/backup",'--entrypoint','/bin/bash',$Context.Config.services.sql.image,'-ec',"tar -cf /backup/$volume.tar -C /source .") | Out-Null
        if (-not (Test-Path -LiteralPath "$checkpoint/$volume.tar" -PathType Leaf) -or (Get-Item -LiteralPath "$checkpoint/$volume.tar").Length -eq 0) { throw 'Required proxy state archive is missing or empty.' }
    }
    $hashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $checkpoint -Recurse -File) { $hashes[[IO.Path]::GetRelativePath($checkpoint,$file.FullName)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    Write-UpdateJson @{Version=1;Status='Complete';BackupId=$manifest.BackupId;InstallationId=$Context.InstallationId;SchemaVersion=$manifest.SchemaVersion;Database='Workbench';Image=$Context.PreviousImage;SourceRevision=$Context.PreviousRevision;SnapshotVolume=$Context.SnapshotVolume;Files=$hashes} "$checkpoint/catalog.json"
}
function Invoke-LocalUpdate($Context) {
    $downtimeStarted = $false
    try {
        Write-UpdateJournal $Context 'stop'
        $downtimeStarted = $true
        Invoke-LocalDocker $Context.Docker ($Context.Compose + @('stop','proxy','app','worker')) | Out-Null
        Assert-UpdateOffline $Context
        Write-UpdateJournal $Context 'checkpoint'
        New-UpdateCheckpoint $Context
        Write-UpdateJournal $Context 'migration'
        $Context.Image = $Context.CandidateImage
        Invoke-LocalDatabase $Context 'migrator' @('migrate')
        Write-UpdateJournal $Context 'publish'
        [IO.File]::Move($Context.CandidateFile,$Context.ComposeFile,$true)
        Write-UpdateJournal $Context 'app'
        Invoke-LocalDocker $Context.Docker ($Context.Compose + @('up','-d','--no-deps','app')) | Out-Null
        Wait-LocalApp $Context
        Write-UpdateJournal $Context 'worker'
        Invoke-LocalDocker $Context.Docker ($Context.Compose + @('run','--rm','--no-deps','worker','--worker','--once')) | Out-Null
        Invoke-LocalDocker $Context.Docker ($Context.Compose + @('up','-d','--no-deps','worker')) | Out-Null
        Write-UpdateJournal $Context 'https'
        Invoke-LocalDocker $Context.Docker ($Context.Compose + @('up','-d','--no-deps','proxy')) | Out-Null
        Assert-UpdateHttps $Context
        Assert-UpdateRunning $Context
        Write-UpdateJournal $Context 'complete' 'Succeeded'
    } catch {
        if (-not $downtimeStarted) { throw 'Could not establish the update journal; running services were left unchanged.' }
        $failedPhase = $Context.Phase
        $Context.FailedPhase = $failedPhase
        try { Write-UpdateJournal $Context 'failed' 'Failed' } catch { Write-Warning 'Could not persist failure journal; preserve the existing journal.' }
        $offline = $false
        try {
            Invoke-LocalDocker $Context.Docker ($Context.Compose + @('stop','proxy','app','worker')) | Out-Null
            Assert-UpdateOffline $Context
            $offline = $true
        } catch { }
        $Context.Offline = $offline
        try { Write-UpdateJournal $Context 'failed' 'Failed' } catch { }
        throw "Update failed during $failedPhase. Workloads verified stopped: $offline. Preserve update.json and $($Context.Release); do not rerun or roll back blindly."
    }
}
