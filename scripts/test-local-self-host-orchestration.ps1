# Copyright (c) 2026 The White Stag Collection.
#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('workbench-setup-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$fixture/bin" -Force | Out-Null
Copy-Item "$PSScriptRoot/local-self-host/test-docker.ps1" "$fixture/bin/docker.ps1"
$oldPath = $env:PATH
$oldFixture = $env:WB_SETUP_FIXTURE_ROOT
try {
    $env:PATH = "$fixture/bin;$oldPath"
    $env:WB_SETUP_FIXTURE_ROOT = $fixture
    # GIVEN a fresh root and simulated Docker, WHEN setup runs, THEN provision all stages without user input.
    @{ TenantName='Overridden tenant'; AdminEmail='qa@example.test'; InstallationRoot="$fixture/install"; HttpPort=28081; HttpsPort=28443 } | ConvertTo-Json | Set-Content "$fixture/settings.json"
    & "$PSScriptRoot/setup-local-self-host.ps1" -ConfigurationFile "$fixture/settings.json" -TenantName "QA O'Brien"
    $state = Get-Content "$fixture/install/installation.json" -Raw | ConvertFrom-Json
    if ($state.Status -ne 'AwaitingWindowsTrust') { throw 'Setup falsely claimed trusted HTTPS.' }
    $config = Get-Content "$fixture/install/compose.json" -Raw | ConvertFrom-Json
    # THEN runtime credentials stay separated and listeners remain on loopback.
    if ($config.services.sql.ports -or $config.services.app.ports -or $config.services.worker.ports) { throw 'Private listener exposed.' }
    if (@($config.services.proxy.ports | Where-Object { $_ -notlike '127.0.0.1:*' }).Count) { throw 'Proxy is not local-only.' }
    foreach ($role in @('app','worker')) {
        if ($config.services.$role.volumes.source -match '(setup|operator|migrator|maintenance)-connection') { throw 'Privileged credential leaked to runtime.' }
        if ($config.services.$role.environment.SSL_CERT_FILE -ne '/etc/ssl/certs/ca-certificates.crt') { throw 'SQL trust override missing.' }
    }
    $commands = @(Get-Content "$fixture/commands.jsonl" | ForEach-Object { ,(ConvertFrom-Json $_ -NoEnumerate) })
    if (-not ($commands | Where-Object { $_ -contains 'bootstrap' -and $_ -contains "QA O'Brien" -and $_ -contains 'qa@example.test' })) { throw 'JSON settings or parameter precedence failed.' }
    $sql = $config.services.sql
    if ('NET_BIND_SERVICE' -notin $sql.cap_add -or 'ALL' -notin $sql.cap_drop) { throw 'SQL capability regression.' }
    $workerStart = $commands | Where-Object { $_ -contains 'up' -and $_ -contains 'worker' }
    if (-not $workerStart) { throw 'Worker never started.' }
    $healthIndex = -1; $workerIndex = -1
    for ($i=0; $i -lt $commands.Count; $i++) {
        if ($commands[$i] -contains '--health-check' -and $healthIndex -eq -1) { $healthIndex=$i }
        if ($commands[$i] -contains '--once' -and $workerIndex -eq -1) { $workerIndex=$i }
    }
    if ($healthIndex -lt 0 -or $workerIndex -le $healthIndex) { throw 'Worker preceded app readiness.' }
    # GIVEN retained storage without containers, WHEN setup starts, THEN reject before creating the root.
    $collisionRoot = [IO.Path]::GetFullPath("$fixture/collision")
    $project = 'workbench-local-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($collisionRoot.ToLowerInvariant()))).Substring(0,10).ToLowerInvariant()
    $env:WB_FIXTURE_COLLISION = "${project}_sql-data"
    $rejected = $false
    try { & "$PSScriptRoot/setup-local-self-host.ps1" -TenantName QA -AdminEmail qa@example.test -InstallationRoot $collisionRoot -HttpPort 28081 -HttpsPort 28443 } catch { $rejected=$true }
    if (-not $rejected -or (Test-Path $collisionRoot)) { throw 'Retained storage was adopted.' }
    Remove-Item Env:WB_FIXTURE_COLLISION
    # GIVEN an existing installation, WHEN setup is retried, THEN refuse without altering secrets.
    $secretHash = (Get-FileHash "$fixture/install/secrets/tenant-proof").Hash
    $rejected = $false
    try { & "$PSScriptRoot/setup-local-self-host.ps1" -TenantName QA -AdminEmail qa@example.test -InstallationRoot "$fixture/install" } catch { $rejected = $true }
    if (-not $rejected -or (Get-FileHash "$fixture/install/secrets/tenant-proof").Hash -ne $secretHash) { throw 'Existing installation was changed.' }
    # GIVEN worker failure, WHEN provisioning reaches it, THEN preserve failed state and stop public workloads.
    $env:WB_FIXTURE_FAIL_WORKER = '1'
    $rejected = $false
    try { & "$PSScriptRoot/setup-local-self-host.ps1" -TenantName QA -AdminEmail qa@example.test -InstallationRoot "$fixture/failed" -HttpPort 28081 -HttpsPort 28443 } catch { $rejected = $true }
    $failed = Get-Content "$fixture/failed/installation.json" -Raw | ConvertFrom-Json
    if (-not $rejected -or $failed.Status -ne 'Failed') { throw 'Worker failure was not contained.' }
    $last = Get-Content "$fixture/commands.jsonl" -Tail 1 | ConvertFrom-Json
    if ('stop' -notin $last -or 'proxy' -notin $last) { throw 'Failure did not stop public workloads.' }
    Write-Host 'Local setup orchestration checks passed (simulated Docker; no live service claimed).'
} finally {
    $env:PATH = $oldPath
    $env:WB_SETUP_FIXTURE_ROOT = $oldFixture
    Remove-Item Env:WB_FIXTURE_FAIL_WORKER -ErrorAction SilentlyContinue
    Remove-Item Env:WB_FIXTURE_COLLISION -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ((Split-Path $resolved -Leaf) -notmatch '^workbench-setup-test-[a-f0-9]{32}$' -or (Split-Path $resolved -Parent) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
