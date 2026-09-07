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

& {
    # GIVEN a real parameters file with spaces in its path and an entirely mocked Azure boundary.
    $fixtureDirectory = Join-Path ([IO.Path]::GetTempPath()) ("workbench bootstrap " + [guid]::NewGuid().ToString('N'))
    $fixturePath = Join-Path $fixtureDirectory 'parameters file.json'
    $document = Get-Content "$PSScriptRoot/main.parameters.example.json" -Raw | ConvertFrom-Json -AsHashtable
    $document.parameters.image.value = 'example.azurecr.io/workbench@sha256:' + ('a' * 64)
    $document.parameters.installationId.value = 'ca434d31-5c6d-44c2-a899-0486d9facd45'
    $document.parameters.sqlAdminObjectId.value = 'bf125b43-8eac-4f23-baca-2a264f11f7df'
    $state = @{ Calls = [Collections.Generic.List[object]]::new(); Active = $true; FailDeployment = $false }
    function Start-Sleep { param($Seconds) }
    function az {
        $state.Calls.Add(@($args))
        $global:LASTEXITCODE = 0
        switch ($args[0..2] -join ' ') {
            'network vnet list' { '[]' }
            'deployment group create' { if ($state.FailDeployment) { $global:LASTEXITCODE = 1 } }
            'containerapp revision list' { @(@{ name='web--entry'; properties=@{ active=$state.Active; replicas=0 } }) | ConvertTo-Json -Depth 5 -AsArray }
            'containerapp revision deactivate' { $state.Active = $false }
            default { throw 'Unexpected Azure command in entry-point test.' }
        }
    }
    $previousExitCode = $global:LASTEXITCODE
    try {
        New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
        $document | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $fixturePath
        # WHEN the documented entry point runs, THEN deployment receives the original file and cleanup completes.
        & "$PSScriptRoot/bootstrap.ps1" -Subscription 'test-subscription' -ResourceGroup 'test-group' -ParametersFile $fixturePath
        $deployment = @($state.Calls | Where-Object { $_[0] -eq 'deployment' })
        if ($deployment.Count -ne 1 -or $deployment[0] -cnotcontains "@$fixturePath" -or $state.Active) {
            throw 'Entry point lost its parameters path or failed to deactivate the revision.'
        }
        # GIVEN deployment fails, WHEN the entry point runs, THEN cleanup runs and failure reaches the caller.
        $state.Calls.Clear(); $state.Active = $true; $state.FailDeployment = $true
        $failure = $null
        try { & "$PSScriptRoot/bootstrap.ps1" -Subscription 'test-subscription' -ResourceGroup 'test-group' -ParametersFile $fixturePath }
        catch { $failure = $_ }
        if (-not $failure -or $failure.Exception.Message -ne 'Bootstrap deployment failed.' -or $state.Active) {
            throw 'Entry-point deployment failure did not preserve cleanup and error propagation.'
        }
        # GIVEN invalid configuration, WHEN the entry point runs, THEN it rejects before any Azure invocation.
        $state.Calls.Clear()
        $document.parameters.image.value = 'example.azurecr.io/workbench:latest'
        $document | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $fixturePath
        $failure = $null
        try { & "$PSScriptRoot/bootstrap.ps1" -Subscription 'test-subscription' -ResourceGroup 'test-group' -ParametersFile $fixturePath }
        catch { $failure = $_ }
        if (-not $failure -or $state.Calls.Count -ne 0) { throw 'Invalid entry-point input reached Azure.' }
    } finally {
        if (Test-Path -LiteralPath $fixturePath) { Remove-Item -LiteralPath $fixturePath }
        if (Test-Path -LiteralPath $fixtureDirectory) { Remove-Item -LiteralPath $fixtureDirectory }
        $global:LASTEXITCODE = $previousExitCode
    }
}
Write-Host 'Bootstrap entry-point checks passed (Azure mocked).'
