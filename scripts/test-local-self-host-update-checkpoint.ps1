# Copyright (c) 2026 The White Stag Collection.
#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/local-self-host/Update.ps1"
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('workbench-checkpoint-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $fixture | Out-Null
function Assert-CheckpointTest($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
try {
    function Protect-LocalDirectory([string]$Path) { $script:protected=$Path }
    function Invoke-LocalSql($Context, [string]$Sql) {
        Assert-CheckpointTest ($Sql -match 'BACKUP DATABASE \[Workbench\].*WITH COPY_ONLY, CHECKSUM;' -and $Sql -match 'RESTORE VERIFYONLY.*WITH CHECKSUM;') 'SQL backup verification options missing.'
        $script:sqlVerified=$true
        if ($script:failure -eq 'sql') { throw 'Injected SQL failure.' }
    }
    function Invoke-LocalDocker($Docker, [string[]]$Arguments) {
        if ($Arguments[0] -eq 'volume' -and $Arguments[1] -eq 'ls') { return }
        if ($Arguments[0] -eq 'volume' -and $Arguments[1] -eq 'create') {
            Assert-CheckpointTest ($Arguments[2] -eq $script:context.SnapshotVolume -and $Arguments[2] -match '^fixture-project_checkpoint-[a-f0-9]{32}$') 'Snapshot volume identity missing.'
            $journal=Get-Content "$($script:context.Root)/update.json" -Raw | ConvertFrom-Json
            Assert-CheckpointTest ($journal.SnapshotVolume -eq $Arguments[2]) 'Snapshot volume was not journaled before creation.'
            $script:volumeCreated=$true
            return
        }
        if ($Arguments[0] -eq 'compose' -and $Arguments -contains 'ps') { return 'fixture-sql' }
        if ($Arguments[0] -eq 'cp') {
            Assert-CheckpointTest $script:sqlVerified 'SQL copy preceded verification.'
            [IO.File]::WriteAllText($Arguments[2], $(if ($script:failure -eq 'empty-backup') {''} else {'synthetic-backup'}))
            return
        }
        if ($Arguments[0] -eq 'run') {
            if ($Arguments[-1] -eq 'mkdir -p /snapshot/blobs; chown -R 1654:1654 /snapshot') {
                Assert-CheckpointTest $script:volumeCreated 'Snapshot initialization preceded volume creation.'
                Assert-CheckpointTest ($Arguments -contains "type=volume,source=$($script:context.SnapshotVolume),target=/snapshot") 'Snapshot initialized wrong volume.'
                New-Item -ItemType Directory "$($script:context.Root)/engine/blobs" | Out-Null
                $script:volumeInitialized=$true
                return
            }
            if ($Arguments[-1] -eq 'cp -R --no-preserve=mode,ownership /snapshot/. /backup/') {
                Assert-CheckpointTest ($Arguments -contains "type=volume,source=$($script:context.SnapshotVolume),target=/snapshot,readonly") 'Snapshot export source was not read-only.'
                Copy-Item "$($script:context.Root)/engine/*" "$($script:context.Checkpoint)" -Recurse -Force
                $blob="$($script:context.Checkpoint)/blobs/$($script:blobName)"
                switch ($script:failure) {
                    'missing-blob' { Remove-Item -LiteralPath $blob }
                    'truncated-blob' { [IO.File]::WriteAllText($blob,'short') }
                    'corrupt-blob' { [IO.File]::WriteAllText($blob,'synthetic-BLOB') }
                }
                return
            }
            $volume = if ($Arguments[-1] -match '/backup/(proxy-(?:data|config))\.tar') { $Matches[1] } else { throw 'Unexpected checkpoint archive.' }
            Assert-CheckpointTest ($Arguments -contains "type=volume,source=$($script:context.Project)_$volume,target=/source,readonly") 'Proxy archive source was not read-only.'
            if ($script:failure -ne "missing-$volume") { [IO.File]::WriteAllText("$($script:context.Checkpoint)/$volume.tar", 'synthetic-archive') }
            return
        }
        throw 'Unexpected checkpoint Docker operation.'
    }
    function Invoke-LocalDatabase($Context, [string]$Role, [string[]]$Command, [string[]]$AdditionalSecrets, [string[]]$MountArguments) {
        Assert-CheckpointTest (Test-Path "$($Context.Checkpoint)/blobs" -PathType Container) 'Snapshot target directory was not created.'
        Assert-CheckpointTest $script:volumeInitialized 'Snapshot engine destination was not initialized.'
        Assert-CheckpointTest ($script:protected -eq $Context.Checkpoint) 'Checkpoint was not protected before snapshot.'
        Assert-CheckpointTest ($Role -eq 'maintenance' -and $Context.Image -eq $Context.PreviousImage) 'Snapshot used wrong role or image.'
        Assert-CheckpointTest (($Command -join '|') -eq 'storage|snapshot|--offline-confirmation|OFFLINE Workbench|--config-file|/run/checkpoint-storage.json|--output-file|/backup/manifest.json') 'Snapshot command contract changed.'
        Assert-CheckpointTest ($AdditionalSecrets.Count -eq 0 -and $MountArguments -contains "type=volume,source=$($Context.Project)_blobs,target=/var/lib/workbench/blobs,readonly") 'Snapshot source access changed.'
        Assert-CheckpointTest ($MountArguments -contains "type=volume,source=$($Context.SnapshotVolume),target=/backup" -and $MountArguments -contains "type=bind,source=$($Context.Checkpoint)/storage.json,target=/run/checkpoint-storage.json,readonly") 'Snapshot output or configuration mount changed.'
        $storage = Get-Content "$($Context.Checkpoint)/storage.json" -Raw | ConvertFrom-Json
        Assert-CheckpointTest ($storage.Storage.InstallationId -eq $Context.InstallationId -and $storage.Target.Storage.Root -eq '/backup/blobs') 'Snapshot storage identity or target changed.'
        if ($script:failure -eq 'snapshot') { throw 'Injected snapshot failure.' }
        $blob="$($Context.Root)/engine/blobs/$($script:blobName)"
        [IO.File]::WriteAllText($blob, 'synthetic-blob')
        $manifest = @{Version=1;BackupId=$script:backupId;InstallationId=$Context.InstallationId;Database='Workbench';SchemaVersion='fixture-schema';Entries=@(@{TenantId=$script:tenantId;RevisionId=$script:revisionId;ProviderAlias='FileSystem';Length=(Get-Item $blob).Length;Sha256=(Get-FileHash $blob).Hash})}
        switch ($script:failure) {
            'bad-installation' { $manifest.InstallationId=[guid]::NewGuid().ToString() }
            'bad-backup-id' { $manifest.BackupId=[guid]::Empty.ToString() }
            'bad-database' { $manifest.Database='Other' }
            'missing-schema' { $manifest.SchemaVersion='' }
            'bad-version' { $manifest.Version=2 }
            'missing-entries' { $manifest.Remove('Entries') }
        }
        Write-UpdateJson $manifest "$($Context.Root)/engine/manifest.json"
    }
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($failure in @('', 'sql', 'empty-backup', 'snapshot', 'bad-installation', 'bad-backup-id', 'bad-database', 'missing-schema', 'bad-version', 'missing-entries', 'missing-proxy-data', 'missing-proxy-config', 'missing-blob', 'truncated-blob', 'corrupt-blob')) {
        # GIVEN retained files and an optional backup, snapshot, or archive failure.
        $script:failure=$failure
        $script:protected=$null
        $script:sqlVerified=$false
        $script:volumeCreated=$false
        $script:volumeInitialized=$false
        $script:backupId=[guid]::NewGuid().ToString()
        $script:tenantId=[guid]::NewGuid()
        $script:revisionId=[guid]::NewGuid()
        $script:blobName=$tenantId.ToString('N') + '-' + $revisionId.ToString('N') + '.b'
        $root=Join-Path $fixture ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory "$root/secrets", "$root/trust", "$root/source/infra/compose" | Out-Null
        foreach ($file in @('secrets/tenant-proof','secrets/data-protection.pfx','trust/sql-ca.crt','trust/caddy-local-root.crt','compose.json','installation.json','Caddyfile','source/infra/compose/mssql.conf')) {
            [IO.File]::WriteAllText("$root/$file", 'synthetic-fixture')
        }
        $script:context=@{Root=$root;Checkpoint="$root/checkpoint";Compose=@('compose');Docker='fake';Project='fixture-project';InstallationId=[guid]::NewGuid().ToString();Image='sha256:' + ('a' * 64);PreviousImage='sha256:' + ('a' * 64);PreviousRevision=('a' * 40);Config=@{services=@{sql=@{image='sql@sha256:' + ('b' * 64)}}}}
        # WHEN a paired checkpoint is created with actual fixture files.
        $rejected=$false
        try { New-UpdateCheckpoint $context } catch { $rejected=$true; if (-not $failure) { $failures.Add("Successful checkpoint failed: $($_.Exception.Message)") } }
        # THEN failures cannot produce a complete catalog.
        if ($failure) {
            if (-not $rejected) { $failures.Add("Failure accepted: $failure") }
            if (Test-Path "$root/checkpoint/catalog.json") { $failures.Add("Complete catalog written after $failure") }
        } elseif (-not $rejected) {
            $catalog = Get-Content "$root/checkpoint/catalog.json" -Raw | ConvertFrom-Json -AsHashtable
            Assert-CheckpointTest ($catalog.Status -eq 'Complete' -and $catalog.BackupId -eq $backupId -and $catalog.InstallationId -eq $context.InstallationId -and $catalog.SchemaVersion -eq 'fixture-schema') 'Catalog lost paired manifest identity.'
            Assert-CheckpointTest ($catalog.Image -eq $context.PreviousImage -and $catalog.SourceRevision -eq $context.PreviousRevision -and $catalog.Database -eq 'Workbench') 'Catalog lost installed release identity.'
            Assert-CheckpointTest ($catalog.SnapshotVolume -eq $context.SnapshotVolume) 'Catalog lost engine snapshot volume identity.'
            foreach ($file in @('database.bak','manifest.json','storage.json',"blobs/$blobName",'secrets/tenant-proof','secrets/data-protection.pfx','trust/sql-ca.crt','trust/caddy-local-root.crt','compose.json','installation.json','Caddyfile','mssql.conf','proxy-data.tar','proxy-config.tar')) {
                $relative=$file.Replace('/',[IO.Path]::DirectorySeparatorChar)
                Assert-CheckpointTest ($catalog.Files[$relative] -eq (Get-FileHash "$root/checkpoint/$file").Hash) 'Checkpoint content hash missing or incorrect.'
            }
            foreach ($file in @('secrets/tenant-proof','secrets/data-protection.pfx','trust/sql-ca.crt','trust/caddy-local-root.crt','compose.json','installation.json','Caddyfile')) {
                Assert-CheckpointTest ((Get-FileHash "$root/$file").Hash -eq (Get-FileHash "$root/checkpoint/$file").Hash) 'Retained recovery material changed during copy.'
            }
        }
    }
    if ($failures.Count) { throw ($failures -join "`n") }
    Write-Host 'Update checkpoint checks passed (15 cases; actual files with simulated SQL, Docker, and snapshot).'
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if ((Split-Path $resolved -Parent) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -or (Split-Path $resolved -Leaf) -notmatch '^workbench-checkpoint-test-[a-f0-9]{32}$') { throw 'Unsafe checkpoint fixture cleanup.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
