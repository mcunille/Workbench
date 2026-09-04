[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScript = Join-Path $repositoryRoot 'scripts/verify.ps1'
$restoreScript = Join-Path $repositoryRoot 'scripts/restore-database.ps1'
$global:workbenchNpmCalls = [Collections.Generic.List[string]]::new()
$global:workbenchDotnetCalls = [Collections.Generic.List[string]]::new()
$global:workbenchSqlcmdCalls = [Collections.Generic.List[string]]::new()
$global:workbenchForceCleanupFailure = $false

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

function global:sqlcmd {
    $arguments = $args -join ' '
    $global:workbenchSqlcmdCalls.Add($arguments)
    $global:LASTEXITCODE = if ($arguments -match 'RESTORE DATABASE' -or
        $global:workbenchForceCleanupFailure) { 1 } else { 0 }
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

    $restoreTestRoot = Join-Path ([IO.Path]::GetTempPath()) "workbench-restore-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $restoreTestRoot | Out-Null
    $connectionFile = Join-Path $restoreTestRoot 'connection.txt'
    Set-Content -LiteralPath $connectionFile -NoNewline `
        'Server=fake;Database=master;User ID=operator;Password=Fake-Restore-Password-1!;TrustServerCertificate=True'
    try {
        try {
            & $restoreScript `
                -ConnectionFile $connectionFile `
                -Database WorkbenchRestoreTest `
                -Source '/fake/backup.bak' `
                -Confirmation 'RESTORE WorkbenchRestoreTest'
            throw 'restore-database.ps1 unexpectedly succeeded in the failure-path test.'
        }
        catch {
            if ($_.Exception.Message -notmatch 'Database restore failed') { throw }
        }

        if ($global:workbenchSqlcmdCalls.Count -ne 2 -or
            $global:workbenchSqlcmdCalls[1] -notmatch 'SET MULTI_USER' -or
            $global:workbenchSqlcmdCalls[1] -match 'RESTORE DATABASE') {
            throw 'restore-database.ps1 did not issue a separate MULTI_USER cleanup after failure.'
        }

        $global:workbenchSqlcmdCalls.Clear()
        $global:workbenchForceCleanupFailure = $true
        try {
            & $restoreScript `
                -ConnectionFile $connectionFile `
                -Database WorkbenchRestoreTest `
                -Source '/fake/backup.bak' `
                -Confirmation 'RESTORE WorkbenchRestoreTest'
            throw 'restore-database.ps1 unexpectedly hid a restore and cleanup failure.'
        }
        catch {
            if ($_.Exception.Message -notmatch 'restore failed, and MULTI_USER cleanup also failed') {
                throw
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $restoreTestRoot -Recurse -Force
    }

    Write-Host 'Verification script command boundaries passed.'
}
finally {
    Remove-Item Function:\node -ErrorAction SilentlyContinue
    Remove-Item Function:\npm -ErrorAction SilentlyContinue
    Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
    Remove-Item Function:\git -ErrorAction SilentlyContinue
    Remove-Item Function:\sqlcmd -ErrorAction SilentlyContinue
    Remove-Variable workbenchNpmCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable workbenchDotnetCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable workbenchSqlcmdCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable workbenchForceCleanupFailure -Scope Global -ErrorAction SilentlyContinue
}
