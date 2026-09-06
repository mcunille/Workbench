# Copyright (c) 2026 The White Stag Collection.
# Offline command fixture, placed on PATH only by test-local-self-host-orchestration.ps1.
$ErrorActionPreference = 'Stop'
$a = @($args)
$root = $env:WB_SETUP_FIXTURE_ROOT
if (-not $root) { throw 'This fixture requires the orchestration test environment.' }
[IO.File]::AppendAllText("$root/commands.jsonl", (ConvertTo-Json -InputObject $a -Compress) + "`n")
if ($a[0] -eq 'info') { 'linux'; exit 0 }
if ($a[0] -eq 'network' -and $a[1] -eq 'ls') { exit 0 }
if ($a[0] -eq 'container') { exit 0 }
if ($a[0] -eq 'volume' -and $a[1] -eq 'ls') {
    if ($env:WB_FIXTURE_COLLISION) { $env:WB_FIXTURE_COLLISION }
    exit 0
}
if ($a[0] -eq 'build') {
    $file = $a[[Array]::IndexOf($a, '--iidfile') + 1]
    [IO.File]::WriteAllText($file, 'sha256:' + ('a' * 64))
}
if ($a[0] -eq 'run' -and $a -contains '/probe.txt') { throw 'Unexpected split mount arguments.' }
if ($a[0] -eq 'run' -and ($a -join ' ') -match 'target=/probe.txt') { 'workbench-mount-probe' }
if ($a[0] -eq 'cp') {
    $destination = $a[2]
    [IO.File]::WriteAllText($destination, 'fixture-public-certificate')
}
if ($a[0] -eq 'compose') {
    $file = $a[[Array]::IndexOf($a, '--file') + 1]
    $config = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
    if ($a -contains 'ps') { 'fixture-proxy' }
    if ($env:WB_FIXTURE_FAIL_WORKER -and $a -contains 'run' -and $a -contains 'worker') { exit 1 }
}
exit 0
