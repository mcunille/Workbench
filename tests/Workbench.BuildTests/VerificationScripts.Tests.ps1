[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScript = Join-Path $repositoryRoot 'scripts/verify.ps1'
$global:workbenchNpmCalls = [Collections.Generic.List[string]]::new()
$global:workbenchDotnetCalls = [Collections.Generic.List[string]]::new()

function global:node {
    if ($args.Count -eq 1 -and $args[0] -eq '--version') { 'v26.7.0' }
    $global:LASTEXITCODE = 0
}

function global:npm {
    if ($args.Count -eq 1 -and $args[0] -eq '--version') {
        '11.19.0'
    }
    else {
        $global:workbenchNpmCalls.Add(($args -join ' '))
    }
    $global:LASTEXITCODE = 0
}

function global:dotnet {
    $arguments = $args -join ' '
    $global:workbenchDotnetCalls.Add($arguments)
    if ($args.Count -eq 1 -and $args[0] -eq '--version') {
        '10.0.400'
        $global:LASTEXITCODE = 0
        return
    }

    $global:LASTEXITCODE = 0
}

function global:git {
    $global:LASTEXITCODE = 0
}

try {
    if ((dotnet --version) -ne '10.0.400' -or
        (node --version) -ne 'v26.7.0' -or
        (npm --version) -ne '11.19.0') {
        throw 'Command shims did not return the expected tool versions.'
    }

    try {
        & $verifyScript -SkipDependencyInstall
        throw 'verify.ps1 unexpectedly completed in the command-shim test.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'Published server assembly is missing') {
            throw
        }
    }

    $installCalls = $global:workbenchNpmCalls | Where-Object { $_ -match '^ci(?: |$)' }
    if ($installCalls) {
        throw "verify.ps1 invoked npm ci despite -SkipDependencyInstall: $($installCalls -join ', ')"
    }

    $serverPublish = $global:workbenchDotnetCalls | Where-Object {
        $_ -match 'publish .*Workbench\.Server\.csproj'
    } | Select-Object -First 1
    if (-not $serverPublish -or $serverPublish -notmatch '(?:^| )-p:BuildClient=false(?: |$)') {
        throw 'test-publish.ps1 did not disable the client rebuild for -SkipClientBuild.'
    }

    Write-Host 'Verification script command boundaries passed.'
}
finally {
    Remove-Item Function:\node -ErrorAction SilentlyContinue
    Remove-Item Function:\npm -ErrorAction SilentlyContinue
    Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
    Remove-Item Function:\git -ErrorAction SilentlyContinue
    Remove-Variable workbenchNpmCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable workbenchDotnetCalls -Scope Global -ErrorAction SilentlyContinue
}
