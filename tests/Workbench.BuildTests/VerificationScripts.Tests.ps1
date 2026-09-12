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
$global:workbenchForceTestFailure = $false

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
        '10.0.401'
        $global:LASTEXITCODE = 0
        return
    }

    $global:LASTEXITCODE = if ($global:workbenchForceTestFailure -and $args[0] -eq 'test') { 1 } else { 0 }
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
    if ((dotnet --version) -ne '10.0.401' -or
        (node --version) -ne 'v26.7.0' -or
        (npm --version) -ne '11.19.0') {
        throw 'Command shims did not return the expected tool versions.'
    }

    # GIVEN the full gate, with partition inventory replacing an unfiltered server run
    # WHEN inspecting the orchestration contract (runtime failures are tested in VerificationStages.Tests.ps1)
    $verifyContent = Get-Content -LiteralPath $verifyScript -Raw
    # THEN one compatible Release build produces OpenAPI, all partitions run, and children share current-run artifacts.
    foreach ($required in @('test-server-partitions.ps1', '-NoBuild', 'Complete-VerificationStages',
        'Publish-VerificationArtifacts', 'New-VerificationBuildReceipt', 'OpenApiDocumentsDirectory',
        'WORKBENCH_VERIFICATION_MANIFEST', 'WORKBENCH_VERIFICATION_RUN')) {
        if (-not $verifyContent.Contains($required)) { throw "Full verification missing required contract: $required" }
    }
    if ([regex]::Matches($verifyContent, '(?m)^\s*dotnet build ').Count -ne 1 -or
        $verifyContent -match '(?m)^\s*dotnet test ' -or $verifyContent -match 'dotnet run') {
        throw 'Full verification must build once and delegate the complete test inventory to isolated processes.'
    }
    # GIVEN client tests sharing the gate runner with .NET formatting and compilation
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($verifyScript, [ref]$tokens, [ref]$parseErrors)
    $clientStage = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Start-VerificationStage' -and
        @($node.CommandElements | Where-Object { $_ -is [Management.Automation.Language.StringConstantExpressionAst] -and $_.Value -eq 'client' }).Count -eq 1
    }, $true)
    $action = @($clientStage.CommandElements | Where-Object { $_ -is [Management.Automation.Language.ScriptBlockExpressionAst] })
    if ($action.Count -ne 1) { throw 'Expected one executable client verification stage.' }
    # The function shim consumes --, but native npm needs it to forward Vitest options.
    $testCommand = $action[0].ScriptBlock.Find({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'npm' -and
        @($node.CommandElements | Where-Object { $_ -is [Management.Automation.Language.StringConstantExpressionAst] -and $_.Value -eq 'test:run' }).Count -eq 1
    }, $true)
    if ($null -eq $testCommand -or $testCommand.CommandElements[-2].Extent.Text -cne '--' -or
        $testCommand.CommandElements[-1].Extent.Text -cne '--maxWorkers=1') {
        throw 'The client worker budget must be forwarded to Vitest through the npm -- separator.'
    }
    $global:workbenchNpmCalls.Clear()
    # WHEN the real stage action invokes the client runner through the command boundary
    & ($action[0].ScriptBlock.GetScriptBlock()) 'client-fixture'
    $clientTestCalls = @($global:workbenchNpmCalls | Where-Object { $_ -like 'run test:run *' })
    # THEN exactly one test run receives a single-worker budget, without retries or relaxed timeouts.
    # PowerShell consumes the native -- separator when npm is replaced by the function shim.
    if ($clientTestCalls.Count -ne 1 -or $clientTestCalls[0] -cne 'run test:run --prefix client-fixture --maxWorkers=1') {
        throw "Concurrent client tests must receive a single-worker budget without retries or relaxed timeouts; received: $($clientTestCalls -join ' | ')."
    }

    # GIVEN an operator requesting an individual migration drill
    foreach ($scenario in @('Clean', 'Upgrade', 'ReversibleRollback', 'RestoreRollback')) {
        $global:workbenchDotnetCalls.Clear()
        # WHEN the standalone scenario is invoked
        & (Join-Path $repositoryRoot 'scripts/verify-migrations.ps1') -Scenario $scenario
        # THEN it still runs a filtered integration test command.
        if ($global:workbenchDotnetCalls.Count -ne 1 -or
            $global:workbenchDotnetCalls[0] -notmatch '^test .*Workbench\.Server\.IntegrationTests\.csproj .*--filter FullyQualifiedName~') {
            throw "Standalone migration scenario '$scenario' did not run its filtered integration tests."
        }
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
    Remove-Variable workbenchForceTestFailure -Scope Global -ErrorAction SilentlyContinue
}
