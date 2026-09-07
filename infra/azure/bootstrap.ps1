# Copyright (c) 2026 The White Stag Collection.
[CmdletBinding()]
param([string] $Subscription, [string] $ResourceGroup, [string] $ParametersFile)

function Invoke-WorkbenchBootstrap {
    param([scriptblock] $Deploy, [scriptblock] $GetRevisions, [scriptblock] $Deactivate,
          [scriptblock] $Wait = { Start-Sleep -Seconds 5 }, [int] $Attempts = 60)
    $deploymentError = $null
    try { & $Deploy } catch { $deploymentError = $_ }
    try {
        $stopped = $false
        for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
            $revisions = @(& $GetRevisions)
            foreach ($revision in $revisions) {
                if ($revision.properties.active) {
                    # Retry later and continue cleaning other revisions if one operation fails.
                    try { & $Deactivate $revision.name } catch { Write-Warning "Deactivation failed for $($revision.name); verification will retry." }
                }
            }
            if ($revisions.Count -gt 0 -and @($revisions | Where-Object { -not $_.name -or $null -eq $_.properties.active -or $_.properties.active -or $null -eq $_.properties.replicas -or $_.properties.replicas -ne 0 }).Count -eq 0) {
                $stopped = $true
                break
            }
            & $Wait
        }
        if (-not $stopped) { throw 'Web revisions have not reached inactive with zero replicas.' }
    } catch {
        throw "Bootstrap incomplete: verify web revision cleanup before continuing. $($_.Exception.Message)"
    }
    if ($deploymentError) { throw $deploymentError }
}

if ($ParametersFile) {
    $ErrorActionPreference = 'Stop'
    if (-not $Subscription -or -not $ResourceGroup) { throw 'Subscription and ResourceGroup are required.' }
    . "$PSScriptRoot/validate-parameters.ps1"
    $document = Get-Content -LiteralPath $ParametersFile -Raw | ConvertFrom-Json -AsHashtable
    Test-WorkbenchAzureParameters $document
    $p = $document.parameters
    if ($p.activate.value -or $p.workerEnabled.value -or $p.publishIngress.value) {
        throw 'Bootstrap requires activate, workerEnabled, and publishIngress false.'
    }
    $webName = $p.prefix.value + '-web'
    # Inline VNet subnet declarations must never remove a retained temporary setup subnet.
    $raw = az network vnet list --subscription $Subscription --resource-group $ResourceGroup -o json
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect existing network before bootstrap.' }
    $networks = ($raw -join "`n") | ConvertFrom-Json
    foreach ($network in $networks | Where-Object name -eq ($p.prefix.value + '-vnet')) {
        if (@($network.subnets | Where-Object name -notin @('apps','endpoints')).Count -gt 0) {
            throw 'Remove temporary subnet dependencies and subnets before redeploying the main template.'
        }
    }
    Invoke-WorkbenchBootstrap -Deploy {
        az deployment group create --subscription $Subscription --resource-group $ResourceGroup --name ($p.prefix.value + '-bootstrap') --mode Incremental --template-file "$PSScriptRoot/main.bicep" --parameters "@$ParametersFile" --output none
        if ($LASTEXITCODE -ne 0) { throw 'Bootstrap deployment failed.' }
    } -GetRevisions {
        $raw = az containerapp revision list --subscription $Subscription --resource-group $ResourceGroup --name $webName --all -o json
        if ($LASTEXITCODE -ne 0) { throw 'Could not verify web revisions.' }
        ($raw -join "`n") | ConvertFrom-Json
    } -Deactivate {
        param($revision)
        az containerapp revision deactivate --subscription $Subscription --resource-group $ResourceGroup --name $webName --revision $revision --output none
        if ($LASTEXITCODE -ne 0) { throw "Could not deactivate $revision." }
    }
    Write-Host 'Bootstrap complete: web revisions inactive with zero replicas; manual jobs have not been started by this script.'
}
