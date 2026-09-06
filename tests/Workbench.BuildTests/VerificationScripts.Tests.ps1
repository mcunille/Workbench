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
$global:workbenchForceRestoreFailure = $true

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
    # GIVEN any privileged SQL call, including cleanup after a failure
    # WHEN the script passes the connection policy to the client
    # THEN encryption and certificate validation are mandatory; credentials stay out of argv.
    if ($args -cnotcontains '-N' -or $args -ccontains '-C' -or $args -ccontains '-P') {
        throw 'Privileged SQL calls must require encryption and certificate validation without password arguments.'
    }
    $arguments = $args -join ' '
    $global:workbenchSqlcmdCalls.Add($arguments)
    $global:LASTEXITCODE = if (($global:workbenchForceRestoreFailure -and $arguments -match 'RESTORE DATABASE') -or
        ($global:workbenchForceCleanupFailure -and $arguments -match 'SET MULTI_USER')) { 1 } else { 0 }
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
        'Server=fake;Database=master;User ID=operator;Password=Fake-Restore-Password-1!;Encrypt=False;TrustServerCertificate=True'
    try {
        # GIVEN a remote connection file requesting weaker TLS and an existing password environment
        # WHEN a backup is requested with the required confirmation
        # THEN the command boundary enforces TLS and restores the caller's password environment.
        $priorPassword = $env:SQLCMDPASSWORD
        $env:SQLCMDPASSWORD = 'test-only-environment-sentinel'
        try {
            & (Join-Path $repositoryRoot 'scripts/backup-database.ps1') `
                -ConnectionFile $connectionFile -Database WorkbenchRestoreTest `
                -Destination '/fake/backup.bak' -Confirmation 'BACKUP WorkbenchRestoreTest'
            if ($global:workbenchSqlcmdCalls.Count -ne 1 -or
                $global:workbenchSqlcmdCalls[0] -notmatch 'WITH COPY_ONLY, CHECKSUM, INIT' -or
                $env:SQLCMDPASSWORD -cne 'test-only-environment-sentinel') {
                throw 'Backup did not preserve its SQL batch or password environment.'
            }
        }
        finally { $env:SQLCMDPASSWORD = $priorPassword }
        $global:workbenchSqlcmdCalls.Clear()

        # GIVEN a trusted remote configuration and a successful SQL restore
        # WHEN the restore and cleanup finish
        # THEN the existing restore marker and MULTI_USER behavior remain intact.
        Set-Content -LiteralPath $connectionFile -NoNewline `
            'Server=tcp:trusted.example,1433;Database=master;User ID=operator;Password=test-only;Encrypt=True;TrustServerCertificate=False'
        $global:workbenchForceRestoreFailure = $false
        & $restoreScript -ConnectionFile $connectionFile -Database WorkbenchRestoreTest `
            -Source '/fake/backup.bak' -Confirmation 'RESTORE WorkbenchRestoreTest'
        if ($global:workbenchSqlcmdCalls.Count -ne 2 -or
            $global:workbenchSqlcmdCalls[0] -notmatch 'WorkbenchRestorePending' -or
            $global:workbenchSqlcmdCalls[1] -notmatch 'SET MULTI_USER') {
            throw 'Successful restore did not preserve its marker and cleanup.'
        }
        $global:workbenchForceRestoreFailure = $true
        $global:workbenchSqlcmdCalls.Clear()

        # GIVEN a restore that fails in SQL
        # WHEN the restore command fails before a pending marker is established
        # THEN no release connection can expose the restored credentials.
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

        if ($global:workbenchSqlcmdCalls.Count -ne 1 -or
            $global:workbenchSqlcmdCalls[0] -match 'SET MULTI_USER') {
            throw 'A failed restore must not issue MULTI_USER release.'
        }

        # GIVEN a successful restore followed by a failed marker verification or access transition
        $global:workbenchSqlcmdCalls.Clear()
        $global:workbenchForceRestoreFailure = $false
        $global:workbenchForceCleanupFailure = $true
        # WHEN the release fails
        try {
            & $restoreScript -ConnectionFile $connectionFile -Database WorkbenchRestoreTest `
                -Source '/fake/backup.bak' -Confirmation 'RESTORE WorkbenchRestoreTest'
            throw 'restore-database.ps1 unexpectedly hid a release failure.'
        }
        catch {
            # THEN the failure is surfaced without another access transition.
            if ($_.Exception.Message -notmatch 'Database restore release failed' -or
                $global:workbenchSqlcmdCalls.Count -ne 2) { throw }
        }
    }
    finally {
        Remove-Item -LiteralPath $restoreTestRoot -Recurse -Force
    }

    # Expected native failures above must not become GitHub Actions' step exit code.
    # Reset only after all assertions pass; unexpected failures still throw.
    $global:LASTEXITCODE = 0
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
    Remove-Variable workbenchForceRestoreFailure -Scope Global -ErrorAction SilentlyContinue
}
