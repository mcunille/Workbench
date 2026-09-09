# Copyright (c) 2026 The White Stag Collection.
param([Parameter(Mandatory)][string] $TemplateFile)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/new-workload-parameters.ps1"
function Assert-True($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Copy-Document($Value) { return ($Value | ConvertTo-Json -Depth 100 | ConvertFrom-Json -AsHashtable) }
function Assert-Rejected([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-True $rejected 'Invalid parameter handoff was accepted.'
}
$template = Get-Content $TemplateFile -Raw | ConvertFrom-Json -AsHashtable
$document = Get-Content "$PSScriptRoot/main.parameters.example.json" -Raw | ConvertFrom-Json -AsHashtable
$document.parameters.image.value = 'example.azurecr.io/workbench@sha256:' + ('a' * 64)
$document.parameters.installationId.value = 'ca434d31-5c6d-44c2-a899-0486d9facd45'
$document.parameters.sqlAdminObjectId.value = 'bf125b43-8eac-4f23-baca-2a264f11f7df'
$outputs = @{
    environmentId = @{ value = '/subscriptions/example/resourceGroups/example/providers/Microsoft.App/managedEnvironments/example' }
    sqlHost = @{ value = 'example.database.windows.net' }
    databaseName = @{ value = 'Workbench' }
    containerUri = @{ value = 'https://example.blob.core.windows.net/workbench' }
    vaultUri = @{ value = 'https://example.vault.azure.net/' }
}
# GIVEN the canonical input omits a main-template default
# WHEN assembling a scoped workload deployment
$result = New-WorkbenchWorkloadParameters $document $template $outputs
# THEN smtpPort is resolved and foundation outputs are included without foundation deployment.
Assert-True ($result.parameters.smtpPort.value -eq 587) 'Missing smtpPort default was not resolved.'
Assert-True ($result.parameters.environmentId.value -eq $outputs.environmentId.value) 'Foundation environment was lost.'
Assert-True ($result.parameters.Count -eq $template.resources.workloads.properties.template.parameters.Count) 'Module parameters are incomplete.'
# GIVEN a reviewed override and explicit pinned public traffic
$document.parameters.smtpPort = @{ value = 2525 }
$document.parameters.activate.value = $true
$document.parameters.grantAccess.value = $true
$document.parameters.proxyTrustMode.value = 'AzureContainerApps'
$document.parameters.publishIngress.value = $true
$document.parameters.publicHost.value = 'example.azurecontainerapps.io'
$document.parameters.publicOrigin.value = 'https://example.azurecontainerapps.io'
$document.parameters.releaseTraffic.value = @(@{ revisionName = 'example--reviewed'; weight = 100 })
# WHEN assembled THEN explicit values, false flags and restrictive access are preserved.
$result = New-WorkbenchWorkloadParameters $document $template $outputs
Assert-True ($result.parameters.smtpPort.value -eq 2525) 'Explicit port was overwritten.'
Assert-True ($result.parameters.workerEnabled.value -eq $false) 'Worker activation changed.'
Assert-True ($result.parameters.releaseTraffic.value[0].revisionName -ceq 'example--reviewed') 'Pinned traffic changed.'
Assert-True ($result.parameters.ingressPolicy.value.mode -ceq 'Restricted') 'Ingress policy changed.'
Assert-True (-not $result.parameters.Contains('sqlAdminObjectId')) 'Non-workload input leaked into output.'
# GIVEN incomplete outputs, a mutable image, an unknown field or unresolved ARM expression
# WHEN assembled THEN fail before producing a deployable document.
$missing = Copy-Document $outputs; $missing.Remove('sqlHost')
Assert-Rejected { New-WorkbenchWorkloadParameters $document $template $missing }
$invalid = Copy-Document $document; $invalid.parameters.image.value = 'example.azurecr.io/workbench:latest'
Assert-Rejected { New-WorkbenchWorkloadParameters $invalid $template $outputs }
$invalid = Copy-Document $document; $invalid.parameters.secret = @{ value = 'do-not-export' }
Assert-Rejected { New-WorkbenchWorkloadParameters $invalid $template $outputs }
$invalid = Copy-Document $document; $invalid.parameters.Remove('location')
Assert-Rejected { New-WorkbenchWorkloadParameters $invalid $template $outputs }
$changed = Copy-Document $template; $changed.resources.workloads.properties.parameters.smtpPort.value = '[unsupported()]'
Assert-Rejected { New-WorkbenchWorkloadParameters $document $changed $outputs }
# GIVEN file inputs and a fresh output WHEN invoking the public script
# THEN it writes a usable file and refuses to overwrite retained configuration.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('workbench-parameters-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
try {
    $inputFile = Join-Path $scratch 'input.json'
    $outputsFile = Join-Path $scratch 'foundation.json'
    $outputFile = Join-Path $scratch 'workloads.json'
    $document | ConvertTo-Json -Depth 100 | Set-Content $inputFile
    $outputs | ConvertTo-Json -Depth 100 | Set-Content $outputsFile
    & "$PSScriptRoot/new-workload-parameters.ps1" -ParametersFile $inputFile -MainTemplateFile $TemplateFile -FoundationOutputsFile $outputsFile -OutputFile $outputFile
    $written = Get-Content $outputFile -Raw | ConvertFrom-Json -AsHashtable
    Assert-True ($written.parameters.smtpPort.value -eq 2525) 'File entry point lost the explicit port.'
    $before = (Get-FileHash $outputFile).Hash
    Assert-Rejected { & "$PSScriptRoot/new-workload-parameters.ps1" -ParametersFile $inputFile -MainTemplateFile $TemplateFile -FoundationOutputsFile $outputsFile -OutputFile $outputFile }
    Assert-True ((Get-FileHash $outputFile).Hash -eq $before) 'Existing output was modified.'
    # GIVEN invalid input WHEN invoking the file entry point THEN no output is published.
    $invalid | ConvertTo-Json -Depth 100 | Set-Content $inputFile
    $badOutput = Join-Path $scratch 'invalid.json'
    Assert-Rejected { & "$PSScriptRoot/new-workload-parameters.ps1" -ParametersFile $inputFile -MainTemplateFile $TemplateFile -FoundationOutputsFile $outputsFile -OutputFile $badOutput }
    Assert-True (-not (Test-Path $badOutput)) 'Invalid input produced an output file.'
} finally {
    if (-not [IO.Path]::GetFullPath($scratch).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected scratch location.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
Write-Host 'Workload parameter assembly checks passed.'
