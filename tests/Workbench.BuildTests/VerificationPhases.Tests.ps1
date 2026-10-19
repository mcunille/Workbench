$ErrorActionPreference = 'Stop'
$phaseScript = Join-Path $PSScriptRoot '../../scripts/verification-phases.ps1'
if (Test-Path $phaseScript) { . $phaseScript }
if (-not (Get-Command Invoke-VerificationPreparation -ErrorAction SilentlyContinue)) { throw 'Shared preparation behavior is missing.' }
$root = Join-Path ([IO.Path]::GetTempPath()) "verification-phases-$([guid]::NewGuid().ToString('N'))"
$priorPath = $env:PATH; $priorFixture = $env:WORKBENCH_PHASE_FIXTURE
$priorManifest = $env:WORKBENCH_VERIFICATION_MANIFEST; $priorRun = $env:WORKBENCH_VERIFICATION_RUN
try {
    New-Item -ItemType Directory -Path "$root/scripts", "$root/docs/operations", "$root/src/Workbench.Client", "$root/tests/Workbench.BrowserTests" -Force | Out-Null
    Set-Content "$root/docs/operations/database-migrations.md" './scripts/verify-migrations.ps1 -Scenario Clean'
    Set-Content "$root/docs/operations/database-backup-restore.md" './scripts/restore-database.ps1'
    Set-Content "$root/README.md" './scripts/dev-up.ps1'
    Set-Content "$root/docs/ARCHITECTURE.md" 'Public recovery remains disabled until'
    foreach ($name in @('dotnet','node','npm','git')) {
        @'
$ErrorActionPreference = 'Stop'
$command = [IO.Path]::GetFileNameWithoutExtension($PSCommandPath)
if ($args[0] -eq '--version') {
    switch ($command) { dotnet { '10.0.401' }; node { 'v26.7.0' }; npm { '11.19.0' } }
    exit 0
}
$fixture = $env:WORKBENCH_PHASE_FIXTURE
$event = "$command $($args -join ' ')"
Set-Content (Join-Path $fixture ([guid]::NewGuid().ToString('N')+'.event')) $event
if ($command -eq 'npm' -and $args -contains 'test:run') { Start-Sleep -Milliseconds 300; Set-Content (Join-Path $fixture 'client-done') ''; exit 0 }
if ($command -eq 'npm' -and $args -contains 'generate:api' -and -not (Test-Path (Join-Path $fixture 'client-done'))) { exit 17 }
if ($command -eq 'dotnet' -and $args -contains 'publish') {
    $output = $args[[Array]::IndexOf($args,'--output')+1]
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $dll = if (($args -join ' ') -match 'Workbench.Database') { 'Workbench.Database.dll' } else { 'Workbench.Server.dll' }
    Set-Content (Join-Path $output $dll) 'current'
}
if ($command -eq 'git' -and $args -contains 'ls-files') { 'README.md' }
exit 0
'@ | Set-Content "$root/$name.ps1"
    }
    @'
$ErrorActionPreference='Stop'
Set-Content (Join-Path $env:WORKBENCH_PHASE_FIXTURE 'published-done') $env:WORKBENCH_VERIFICATION_MANIFEST
if (Test-Path (Join-Path $env:WORKBENCH_PHASE_FIXTURE 'fail-published')) { throw 'deliberate published failure' }
'@ | Set-Content "$root/scripts/test-publish.ps1"
    # Browser stage uses npm test; add a fixture completion marker at that native boundary.
    Add-Content "$root/npm.ps1" ''
    $npm = Get-Content "$root/npm.ps1" -Raw
    $npm = $npm.Replace("if (`$command -eq 'npm' -and `$args -contains 'test:run')", "if (`$command -eq 'npm' -and `$args[0] -eq 'test') { Start-Sleep -Milliseconds 500; Set-Content (Join-Path `$fixture 'browser-done') ''; exit 0 }`nif (`$command -eq 'npm' -and `$args -contains 'test:run')")
    Set-Content "$root/npm.ps1" $npm
    $env:PATH = "$root$([IO.Path]::PathSeparator)$priorPath"; $env:WORKBENCH_PHASE_FIXTURE=$root
    # GIVEN executable native-boundary fixtures WHEN preparing THEN the client is joined before generation and one callback starts server work.
    $counter = [Collections.Generic.List[int]]::new()
    $prepared = Invoke-VerificationPreparation -RepositoryRoot $root -RunId fixture -RunRoot "$root/run" -SkipDependencyInstall -OnServerReady { $counter.Add(1) }
    if ($counter.Count -ne 1 -or -not $prepared.ClientReceipt.Succeeded -or (Test-Path "$root/run/gate.json")) { throw 'Preparation violated incomplete-phase accounting.' }
    $events = @(Get-ChildItem $root -Filter '*.event' | ForEach-Object { Get-Content $_.FullName })
    if (@($events | Where-Object { $_ -like 'dotnet build *' }).Count -ne 1 -or @($events | Where-Object { $_ -like 'npm run test:run *--maxWorkers=1' }).Count -ne 1) { throw 'Shared preparation duplicated build or lost client budget.' }
    # GIVEN a failed published stage WHEN release is joined THEN browser finishes and caller environment is restored.
    $env:WORKBENCH_VERIFICATION_MANIFEST='sentinel'; $env:WORKBENCH_VERIFICATION_RUN='sentinel-run'
    Set-Content "$root/fail-published" ''
    $rejected=$false
    try { Invoke-VerificationRelease $root fixture $prepared.LocalManifestPath "$root/release" | Out-Null } catch { if ($_.Exception.Message -notmatch 'published') { throw }; $rejected=$true }
    if (-not $rejected -or -not (Test-Path "$root/browser-done") -or -not (Test-Path "$root/published-done") -or $env:WORKBENCH_VERIFICATION_MANIFEST -cne 'sentinel' -or $env:WORKBENCH_VERIFICATION_RUN -cne 'sentinel-run') { throw 'Release did not join failure or restore environment.' }
    Write-Host 'Shared verification phase execution passed.'
}
finally {
    $env:PATH=$priorPath; $env:WORKBENCH_PHASE_FIXTURE=$priorFixture
    $env:WORKBENCH_VERIFICATION_MANIFEST=$priorManifest; $env:WORKBENCH_VERIFICATION_RUN=$priorRun
    $resolved=[IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('verification-phases-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
