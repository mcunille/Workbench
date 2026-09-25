[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$restoreScript = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/restore-database.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "workbench-restore-batch-$([Guid]::NewGuid().ToString('N'))"
$connectionFile = Join-Path $testRoot 'connection.txt'
$previousPassword = $env:SQLCMDPASSWORD
$global:restoreBatchCalls = [Collections.Generic.List[string]]::new()
$global:restoreCommandThrows = $false
function global:sqlcmd {
    if ($args -cnotcontains '-N' -or $args -ccontains '-C' -or $args -ccontains '-P' -or
        @($args | Where-Object { ([string]$_).Contains('Fake-Test-Only-1!', [StringComparison]::Ordinal) }).Count -ne 0) {
        $global:restoreTlsSafe = $false
    }
    $serverIndex = [Array]::IndexOf($args, '-S')
    if ($serverIndex -lt 0 -or $args[$serverIndex + 1] -cne $global:restoreExpectedServer) {
        throw 'Restore must use the configured server.'
    }
    $queryIndex = [Array]::IndexOf($args, '-Q')
    if (@($args | Where-Object { $_ -ceq '-Q' }).Count -ne 1 -or $queryIndex + 2 -ne $args.Count) {
        throw 'Expected a single SQL batch argument after -Q.'
    }
    $global:restoreBatchCalls.Add([string]$args[$queryIndex + 1])
    if ($global:restoreCommandThrows) { throw 'Injected command exception.' }
    $global:LASTEXITCODE = if ($global:restoreBatchCalls.Count -eq 1) {
        [int]$global:restoreBatchFails
    } else { [int]$global:restoreReleaseFails }
}
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    # Each reachable outcome also exercises a distinct connection TLS configuration.
    $outcomes = @(
        @{ Name = 'success'; RestoreFails = $false; ReleaseFails = $false; Calls = 2
            Server = 'tcp:trusted.example,1433'; Tls = ';Encrypt=True;TrustServerCertificate=False'; Error = $null },
        @{ Name = 'restore failure'; RestoreFails = $true; ReleaseFails = $false; Calls = 1
            Server = 'fake'; Tls = ';Encrypt=False;TrustServerCertificate=True'
            Error = 'Database restore failed; access was not released. Keep traffic stopped and recover with a privileged connection.' },
        @{ Name = 'release failure'; RestoreFails = $false; ReleaseFails = $true; Calls = 2
            Server = 'fake'; Tls = ''
            Error = 'Database restore release failed; keep traffic stopped and inspect the pending marker and access mode.' }
    )
    foreach ($outcome in $outcomes) {
        # GIVEN a quoted backup path and a reachable restore/release outcome.
        # AND an existing password environment value.
        Set-Content -LiteralPath $connectionFile "Server=$($outcome.Server);Database=master;User ID=operator;Password=Fake-Test-Only-1!$($outcome.Tls)"
        $global:restoreExpectedServer = $outcome.Server
        $global:restoreBatchFails = $outcome.RestoreFails
        $global:restoreReleaseFails = $outcome.ReleaseFails
        $global:restoreBatchCalls.Clear()
        $global:restoreTlsSafe = $true
        $env:SQLCMDPASSWORD = 'prior-test-password'
        $failure = $null

        # WHEN restoring the explicitly confirmed database.
        try {
            & $restoreScript -ConnectionFile $connectionFile -Database WorkbenchRestoreTest `
                -Source "/fake/operator's backup.bak" -Confirmation 'RESTORE WorkbenchRestoreTest'
        }
        catch { $failure = $_ }

        # THEN the actual -Q argument contains executable SQL without surrounding quotes.
        if ($global:restoreBatchCalls.Count -ne $outcome.Calls) { throw "Unexpected command count for $($outcome.Name); failed restores must never attempt release." }
        # AND every outcome requires encrypted, certificate-validated transport without password arguments.
        if (-not $global:restoreTlsSafe) { throw 'Restore and release must enforce authenticated TLS without password arguments.' }
        $batch = $global:restoreBatchCalls[0].Trim()
        if ($batch -match 'SET MULTI_USER') { throw 'The restore batch must never release restricted access.' }
        if (-not $batch.StartsWith('ALTER DATABASE [WorkbenchRestoreTest] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;') -or
            -not $batch.EndsWith('INSERT INTO [Security].[WorkbenchRestorePending] ([Id], [IsPending]) VALUES (1, 1);')) {
            throw 'Restore batch must start and end with SQL statements, without surrounding literal quotes.'
        }
        # AND interpolation, escaped paths, and both pending-marker branches are preserved.
        foreach ($statement in @(
            "RESTORE DATABASE [WorkbenchRestoreTest] FROM DISK = N'/fake/operator''s backup.bak' WITH REPLACE, RECOVERY, RESTRICTED_USER;",
            'USE [WorkbenchRestoreTest];',
            "IF SCHEMA_ID(N'Security') IS NULL EXEC(N'CREATE SCHEMA [Security]');",
            "IF OBJECT_ID(N'[Security].[WorkbenchRestorePending]', N'U') IS NULL",
            'CREATE TABLE [Security].[WorkbenchRestorePending]',
            '[Id] tinyint NOT NULL CONSTRAINT [PK_WorkbenchRestorePending] PRIMARY KEY,',
            '[IsPending] bit NOT NULL,',
            'CONSTRAINT [CK_WorkbenchRestorePending_Singleton] CHECK ([Id] = 1)',
            'IF EXISTS (SELECT 1 FROM [Security].[WorkbenchRestorePending] WHERE [Id] = 1)',
            'UPDATE [Security].[WorkbenchRestorePending] SET [IsPending] = 1 WHERE [Id] = 1;'
        )) {
            if (-not $batch.Contains($statement, [StringComparison]::Ordinal)) { throw "Missing SQL: $statement" }
        }
        # AND only successful restoration can verify the marker and release restricted access.
        if (-not $outcome.RestoreFails) {
            $release = ($global:restoreBatchCalls[1].Trim() -replace '\s+', ' ')
            $expectedRelease = "USE [WorkbenchRestoreTest]; IF NOT EXISTS (SELECT 1 FROM [Security].[WorkbenchRestorePending] WHERE [Id] = 1 AND [IsPending] = 1) " +
                "THROW 51000, 'Restore pending marker is not established; access remains restricted.', 1; USE [master]; " +
                'ALTER DATABASE [WorkbenchRestoreTest] SET MULTI_USER WITH ROLLBACK IMMEDIATE;'
            if ($release -cne $expectedRelease) {
                throw 'Release must independently verify the pending marker before MULTI_USER.'
            }
        }
        if ($env:SQLCMDPASSWORD -cne 'prior-test-password') { throw 'Previous password environment was not restored.' }
        # AND restore and release failures retain their distinct error reports.
        $expected = $outcome.Error
        if (($null -eq $expected -and $null -ne $failure) -or
            ($null -ne $expected -and ($null -eq $failure -or $failure.Exception.Message -cne $expected))) {
            throw 'Unexpected restore outcome.'
        }
    }
    # GIVEN a client exception before the restore result can be confirmed.
    $global:restoreCommandThrows = $true
    $global:restoreBatchCalls.Clear()
    $global:restoreTlsSafe = $true
    $env:SQLCMDPASSWORD = 'prior-test-password'
    $failure = $null
    # WHEN sqlcmd throws instead of returning an exit code.
    try {
        & $restoreScript -ConnectionFile $connectionFile -Database WorkbenchRestoreTest `
            -Source '/fake/backup.bak' -Confirmation 'RESTORE WorkbenchRestoreTest'
    }
    catch { $failure = $_ }
    # THEN no release occurs and the password environment is restored.
    if ($null -eq $failure -or $failure.Exception.Message -ne 'Injected command exception.' -or
        $global:restoreBatchCalls.Count -ne 1 -or -not $global:restoreTlsSafe -or $env:SQLCMDPASSWORD -cne 'prior-test-password') {
        throw 'Command exception must preserve restricted access and the caller environment.'
    }
    $global:LASTEXITCODE = 0
    Write-Host 'Restore SQL batches passed: three reachable outcomes and one command exception.'
}
finally {
    $env:SQLCMDPASSWORD = $previousPassword
    Remove-Item Function:\sqlcmd -ErrorAction SilentlyContinue
    Remove-Variable restoreBatchCalls, restoreBatchFails, restoreReleaseFails, restoreTlsSafe, restoreCommandThrows, restoreExpectedServer -Scope Global -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected test cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
