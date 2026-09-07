# Copyright (c) 2026 The White Stag Collection.
#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$implementation = "$PSScriptRoot/local-self-host/Update.ps1"
if (Test-Path $implementation) { . $implementation }
if (-not (Get-Command Invoke-LocalUpdate -ErrorAction SilentlyContinue)) { throw 'Missing behavior: retained installations cannot be updated.' }
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('workbench-update-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $fixture | Out-Null
try {
    function Write-UpdateJournal($Context, $Phase, $Status = 'Running') {
        $script:events.Add("journal:$Phase`:$Status")
        $Context.Phase = $Phase
        if ($script:failure -eq 'journal' -and $Phase -eq 'stop') { throw 'Injected journal failure' }
    }
    function Invoke-LocalDocker($Docker, $Arguments) {
        $action = if ($Arguments -contains 'stop') { 'stop' } elseif ($Arguments -contains '--once') { 'worker-once' } elseif ($Arguments -contains 'up') { 'start:' + (($Arguments | Select-Object -Last 1) -join '') } else { 'docker' }
        $script:events.Add($action)
        if ($action -eq $script:failure) { throw 'Injected failure' }
    }
    function Assert-UpdateOffline($Context) { $script:events.Add('offline'); if ($script:failure -eq 'offline') { throw 'Injected failure' } }
    function New-UpdateCheckpoint($Context) { $script:events.Add('checkpoint'); if ($script:failure -eq 'checkpoint') { throw 'Injected failure' } }
    function Invoke-LocalDatabase($Context, $Role, $Command) {
        Assert ($Role -eq 'migrator') 'Migration used privileged setup identity.'
        $script:events.Add('migrate'); if ($script:failure -eq 'migrate') { throw 'Injected failure' }
    }
    function Wait-LocalApp($Context) { $script:events.Add('ready'); if ($script:failure -eq 'ready') { throw 'Injected failure' } }
    function Assert-UpdateHttps($Context) { $script:events.Add('https'); if ($script:failure -eq 'https') { throw 'Injected failure' } }
    function Assert-UpdateRunning($Context) { $script:events.Add('running'); if ($script:failure -eq 'running') { throw 'Injected failure' } }
    foreach ($failure in @('', 'journal', 'stop', 'offline', 'checkpoint', 'migrate', 'ready', 'worker-once', 'start:worker', 'start:proxy', 'https', 'running')) {
        # GIVEN a retained installation and an optional failing update phase.
        $script:failure = $failure
        $script:events = [Collections.Generic.List[string]]::new()
        $directory = Join-Path $fixture ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $directory | Out-Null
        [IO.File]::WriteAllText("$directory/compose.json", 'previous')
        [IO.File]::WriteAllText("$directory/candidate.json", 'candidate')
        $context = @{ Root=$directory; Release=$directory; ComposeFile="$directory/compose.json"; CandidateFile="$directory/candidate.json"; Compose=@('compose'); Docker='fake'; Image='old'; CandidateImage='new' }
        # WHEN the offline update runs.
        $rejected = $false
        try { Invoke-LocalUpdate $context } catch { $rejected = $true }
        if (-not $failure) {
            # THEN the checkpoint precedes migration and readiness precedes workers and ingress.
            Assert (-not $rejected) 'Successful update was rejected.'
            foreach ($pair in @(@('offline','checkpoint'), @('checkpoint','migrate'), @('migrate','ready'), @('ready','worker-once'), @('worker-once','start:worker'), @('start:worker','start:proxy'), @('https','journal:complete:Succeeded'))) {
                Assert ($events.IndexOf($pair[0]) -ge 0 -and $events.IndexOf($pair[1]) -gt $events.IndexOf($pair[0])) "Invalid phase order: $pair"
            }
            Assert ((Get-Content "$directory/compose.json" -Raw) -eq 'candidate') 'Candidate was not published.'
        } elseif ($failure -eq 'journal') {
            # THEN failure to establish durable intent must not stop the live installation.
            Assert $rejected 'Journal failure was ignored.'
            Assert (-not ($events -contains 'stop')) 'Services stopped before a durable update journal existed.'
        } else {
            # THEN every failure is reported, journaled, and followed by containment; no rollback occurs.
            Assert $rejected "Failure ignored: $failure"
            Assert ($events -contains 'journal:failed:Failed') "Failure not journaled: $failure"
            Assert ($events[-2] -eq 'offline' -or $events[-2] -eq 'stop') "Missing containment: $failure"
            Assert ($context.FailedPhase -and $context.ContainsKey('Offline')) 'Failure recovery evidence was lost.'
            if ($failure -in @('stop','offline','checkpoint')) {
                Assert (-not ($events -contains 'migrate')) 'Migration ran without an offline checkpoint.'
                Assert ((Get-Content "$directory/compose.json" -Raw) -eq 'previous') 'Configuration changed before migration.'
            }
        }
    }
    Write-Host 'Update phase and failure checks passed (injected operations; no live service claimed).'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ((Split-Path $resolved -Parent) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -or (Split-Path $resolved -Leaf) -notmatch '^workbench-update-test-[a-f0-9]{32}$') { throw 'Unsafe fixture cleanup.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
