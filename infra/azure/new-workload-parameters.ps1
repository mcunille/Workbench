# Copyright (c) 2026 The White Stag Collection.
param([string] $ParametersFile, [string] $MainTemplateFile, [string] $FoundationOutputsFile, [string] $OutputFile)

function New-WorkbenchWorkloadParameters([hashtable] $Document, [hashtable] $Template, [hashtable] $FoundationOutputs) {
    . "$PSScriptRoot/validate-parameters.ps1"
    $resolved = @{}
    foreach ($name in $Document.parameters.Keys) {
        if (-not $Template.parameters.Contains($name) -or
            -not $Document.parameters[$name].Contains('value') -or
            $Document.parameters[$name].Count -ne 1) {
            throw "Unknown parameter or nonliteral input: $name"
        }
    }
    foreach ($name in $Template.parameters.Keys) {
        $definition = $Template.parameters[$name]
        if ($definition.type -in @('secureString', 'secureObject')) { throw 'Secure parameters cannot be exported.' }
        if ($Document.parameters.Contains($name)) { $value = $Document.parameters[$name].value }
        elseif ($definition.Contains('defaultValue')) { $value = $definition.defaultValue }
        else { throw "Missing main parameter: $name" }
        if ($null -eq $value -or ($value -is [string] -and $value.StartsWith('['))) {
            throw "Supply an explicit literal value for main parameter: $name"
        }
        $resolved[$name] = @{ value = $value }
    }
    Test-WorkbenchAzureParameters @{ parameters = $resolved }
    $workloads = $Template.resources.workloads.properties
    if (-not $workloads.parameters -or -not $workloads.template.parameters) { throw 'Expected compiled workloads module is missing.' }
    $result = @{}
    foreach ($name in $workloads.template.parameters.Keys) {
        $expression = $workloads.parameters[$name].value
        if ($expression -match "^\[parameters\('([^']+)'\)\]$") {
            $source = $Matches[1]
            if (-not $resolved.Contains($source)) { throw "Unresolved input for: $name" }
            $value = $resolved[$source].value
        } elseif ($expression -match "^\[reference\('foundation'\)\.outputs\.([A-Za-z0-9]+)\.value\]$") {
            $source = $Matches[1]
            if (-not $FoundationOutputs.Contains($source) -or
                $FoundationOutputs[$source].value -isnot [string] -or
                [string]::IsNullOrWhiteSpace($FoundationOutputs[$source].value)) {
                throw "Missing foundation output: $source"
            }
            $value = $FoundationOutputs[$source].value
        } else { throw "Unsupported module binding for: $name" }
        $result[$name] = @{ value = $value }
    }
    return @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'; contentVersion = '1.0.0.0'; parameters = $result }
}

if ($ParametersFile -or $MainTemplateFile -or $FoundationOutputsFile -or $OutputFile) {
    $ErrorActionPreference = 'Stop'
    if (-not ($ParametersFile -and $MainTemplateFile -and $FoundationOutputsFile -and $OutputFile)) {
        throw 'ParametersFile, MainTemplateFile, FoundationOutputsFile and OutputFile are required.'
    }
    $result = New-WorkbenchWorkloadParameters `
        (Get-Content -LiteralPath $ParametersFile -Raw | ConvertFrom-Json -AsHashtable) `
        (Get-Content -LiteralPath $MainTemplateFile -Raw | ConvertFrom-Json -AsHashtable) `
        (Get-Content -LiteralPath $FoundationOutputsFile -Raw | ConvertFrom-Json -AsHashtable)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Depth 100))
    $stream = [IO.File]::Open([IO.Path]::GetFullPath($OutputFile), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    Write-Host 'Workload parameters prepared; inspect the deployment preview before approval. No Azure changes made.'
}
