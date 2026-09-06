[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$restoreScript = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/restore-database.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "workbench-restore-batch-$([Guid]::NewGuid().ToString('N'))"
$connectionFile = Join-Path $testRoot 'connection.txt'
$previousPassword = $env:SQLCMDPASSWORD
$global:restoreBatchCalls = [Collections.Generic.List[string]]::new()
function global:sqlcmd {
    if ($args -cnotcontains '-N' -or $args -ccontains '-C' -or $args -ccontains '-P') {
        $global:restoreTlsSafe = $false
    }
    $queryIndex = [Array]::IndexOf($args, '-Q')
    if ($queryIndex -lt 0 -or $queryIndex + 2 -ne $args.Count) {
        throw 'Expected a single SQL batch argument after -Q.'
    }
    $global:restoreBatchCalls.Add([string]$args[$queryIndex + 1])
    if ($global:restoreCommandThrows) { throw 'Injected command exception.' }
    $global:LASTEXITCODE = if ($global:restoreBatchCalls.Count -eq 1) {
        [int]$global:restoreBatchFails
    } else { [int]$global:restoreCleanupFails }
}
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Set-Content -LiteralPath $connectionFile 'Server=fake;Database=master;User ID=operator;Password=Fake-Test-Only-1!'
    foreach ($restoreFails in @($false, $true)) {
        foreach ($cleanupFails in @($false, $true)) {
            # GIVEN a quoted backup path and independent restore and cleanup outcomes.
            # AND an existing password environment value.
            $global:restoreBatchFails = $restoreFails
            $global:restoreCleanupFails = $cleanupFails
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
            $expectedCalls = if ($restoreFails) { 1 } else { 2 }
            if ($global:restoreBatchCalls.Count -ne $expectedCalls) { throw 'A failed restore must never attempt MULTI_USER release.' }
            # AND every outcome requires encrypted, certificate-validated transport without password arguments.
            if (-not $global:restoreTlsSafe) { throw 'Restore and cleanup must enforce authenticated TLS.' }
            $batch = $global:restoreBatchCalls[0].Trim()
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
            if (-not $restoreFails) {
                $release = $global:restoreBatchCalls[1]
                if ($release -notmatch 'IF NOT EXISTS' -or $release -notmatch '\[IsPending\] = 1' -or
                    $release -notmatch 'THROW 51000' -or $release -notmatch 'SET MULTI_USER' -or
                    $release.IndexOf('THROW 51000') -gt $release.IndexOf('SET MULTI_USER')) {
                    throw 'Release must independently verify the pending marker before MULTI_USER.'
                }
            }
            if ($env:SQLCMDPASSWORD -cne 'prior-test-password') { throw 'Previous password environment was not restored.' }
            # AND single and combined failures retain their distinct error reports.
            $expected = if ($restoreFails) { 'Database restore failed; access was not released. Keep traffic stopped and recover with a privileged connection.'
            } elseif ($cleanupFails) { 'Database restore release failed; keep traffic stopped and inspect the pending marker and access mode.'
            } else { $null }
            if (($null -eq $expected -and $null -ne $failure) -or
                ($null -ne $expected -and ($null -eq $failure -or $failure.Exception.Message -cne $expected))) {
                throw 'Unexpected restore outcome.'
            }
        }
    }
    # GIVEN a client exception before the restore result can be confirmed.
    $global:restoreCommandThrows = $true
    $global:restoreBatchCalls.Clear()
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
        $global:restoreBatchCalls.Count -ne 1 -or $env:SQLCMDPASSWORD -cne 'prior-test-password') {
        throw 'Command exception must preserve restricted access and the caller environment.'
    }
    $global:LASTEXITCODE = 0
    Write-Host 'Restore SQL batch and all four command outcomes passed.'
}
finally {
    $env:SQLCMDPASSWORD = $previousPassword
    Remove-Item Function:\sqlcmd -ErrorAction SilentlyContinue
    Remove-Variable restoreBatchCalls, restoreBatchFails, restoreCleanupFails, restoreTlsSafe, restoreCommandThrows -Scope Global -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected test cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
