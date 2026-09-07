# Copyright (c) 2026 The White Stag Collection.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/bootstrap.ps1"
# GIVEN deployment fails after creating an active revision WHEN bootstrap runs THEN cleanup still deactivates it
$script:active = $true
$failed = $false
try {
    Invoke-WorkbenchBootstrap -Deploy { throw 'deployment-failed' } -GetRevisions { @(@{ name='web--one'; properties=@{ active=$script:active; replicas=0 } }) } -Deactivate { param($name) $script:active=$false } -Wait {} -Attempts 2
} catch { $failed = $true }
if (-not $failed -or $script:active) { throw 'Failed deployment did not fail closed with cleanup.' }
# GIVEN replicas drain asynchronously WHEN bootstrap runs THEN completion waits for zero replicas
$script:reads=0
Invoke-WorkbenchBootstrap -Deploy {} -GetRevisions { $script:reads++; @(@{ name='web--one'; properties=@{ active=($script:reads -eq 1); replicas= [int]($script:reads -lt 3) } }) } -Deactivate {} -Wait {} -Attempts 3
if ($script:reads -ne 3) { throw 'Bootstrap completed before replicas drained.' }
# GIVEN deactivation never completes WHEN attempts expire THEN bootstrap must fail
$failed=$false
try { Invoke-WorkbenchBootstrap -Deploy {} -GetRevisions { @(@{name='web--one';properties=@{active=$true;replicas=1}}) } -Deactivate {} -Wait {} -Attempts 2 } catch { $failed=$true }
if (-not $failed) { throw 'Active workload reported bootstrap success.' }
Write-Host 'Bootstrap lifecycle checks passed.'
# GIVEN one deactivation fails WHEN cleanup runs THEN other revisions are still attempted
$script:secondAttempted=$false
try {
    Invoke-WorkbenchBootstrap -Deploy {} -GetRevisions { @(@{name='first';properties=@{active=$true;replicas=1}},@{name='second';properties=@{active=$true;replicas=1}}) } -Deactivate { param($name) if($name -eq 'first'){throw 'failed'}; $script:secondAttempted=$true } -Wait {} -Attempts 1
} catch {}
if(-not $script:secondAttempted){throw 'Cleanup abandoned remaining revisions.'}
# GIVEN no revision evidence WHEN bootstrap verifies THEN it cannot report stopped compute
$failed=$false
try { Invoke-WorkbenchBootstrap -Deploy {} -GetRevisions { @() } -Deactivate {} -Wait {} -Attempts 1 } catch { $failed=$true }
if(-not $failed){throw 'Missing revision evidence reported bootstrap success.'}
