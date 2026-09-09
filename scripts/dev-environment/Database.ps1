function Initialize-DevSecrets($Context) {
    $root = "$($Context.Root)/secrets"
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    # Directory ACL is secured by the caller before any secret is generated. Never rotate on retries.
    foreach ($name in @('sql-bootstrap','web','operator','migrator','admin')) {
        $path = "$root/$name-password"
        if (-not (Test-Path -LiteralPath $path)) {
            if ($Context.State.SecretsReady) { throw 'Retained secret is missing. Restore its private copy or explicitly destroy this disposable environment.' }
            [IO.File]::WriteAllText($path, ('Wb-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) + '-aA9!'))
        }
    }
    if (-not (Test-Path -LiteralPath "$root/tenant-proof")) {
        if ($Context.State.SecretsReady) { throw 'Retained tenant proof key is missing.' }
        [IO.File]::WriteAllText("$root/tenant-proof", [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    }
    foreach ($role in @('setup','web','operator','migrator')) {
        $name = if ($role -eq 'setup') { 'sql-bootstrap' } else { $role }
        $user = if ($role -eq 'setup') { 'sa' } else { "workbench_${role}_local" }
        $password = [IO.File]::ReadAllText("$root/$name-password")
        $connection = New-WorkbenchSqlConnection 'tcp:sql,1433' 'Workbench' $user $password $true
        [IO.File]::WriteAllText("$root/$role-connection", $connection)
    }
    [IO.File]::WriteAllText("$root/login.txt", "Email: $($Context.State.AdminEmail)`nPassword: $([IO.File]::ReadAllText("$root/admin-password"))`n")
    $Context.State.SecretsReady = $true
    Write-DevState $Context
}
function Invoke-DevDatabase($Context, [string]$Role, [string[]]$Command, [string[]]$Secrets = @()) {
    # A crashed tool is reaped only after validating the same ownership as long-lived services.
    Remove-DevContainer $Context tool
    Set-DevPhase $Context "database-$($Command[0])"
    $labels = Get-DevLabels $Context.State
    $arguments = @('--name',(Get-DevResourceName $Context tool))
    foreach ($key in $labels.Keys) { $arguments += @('--label',"$key=$($labels[$key])") }
    $databaseContext = @{Docker=$Context.Docker; Root=$Context.Root; Image=$Context.State.Image; Network=(Get-DevResourceName $Context network)}
    Invoke-DatabaseContainer $databaseContext $Role $Command $Secrets $arguments
}
function Invoke-DevSql($Context, [string]$Sql) {
    $container = Get-DevResource $Context container sql
    if (-not $container) { throw 'Owned SQL container is missing.' }
    $command = 'export SQLCMDPASSWORD="$(cat /run/secrets/sql-bootstrap-password)"; exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d master -C -b -l 3 -t 15'
    $Sql | & $Context.Docker.Source exec -i $container.Id /bin/bash -ec $command *> $null
    return ($LASTEXITCODE -eq 0)
}
function Initialize-DevDatabase($Context) {
    Set-DevPhase $Context 'sql-readiness'
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    do {
        # Master accepts connections before retained user databases necessarily finish recovery.
        if (Invoke-DevSql $Context "IF EXISTS (SELECT 1 FROM sys.databases WHERE name=N'Workbench' AND state_desc <> N'ONLINE') THROW 50000, 'Waiting for development database recovery.', 1; IF DB_ID(N'Workbench') IS NOT NULL EXEC(N'USE [Workbench]; SELECT 1;'); SELECT 1;") { $ready = $true; break }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'SQL did not become ready within three minutes.' }
    Set-DevPhase $Context 'database-initialize'
    $initialize = if ($Context.State.Provisioned) {
        "IF DB_ID(N'Workbench') IS NULL THROW 50000, 'Retained database is missing; explicit reset required.', 1;"
    } else { "EXEC sp_configure 'contained database authentication', 1; RECONFIGURE; IF DB_ID(N'Workbench') IS NULL BEGIN CREATE DATABASE [Workbench]; END; ALTER DATABASE [Workbench] SET CONTAINMENT = PARTIAL;" }
    if (-not (Invoke-DevSql $Context $initialize)) { throw 'Development database initialization failed; retained data is never replaced automatically.' }
    $inspectCommand = @('development','inspect','--environment','Development')
    $report = Invoke-DevDatabase $Context setup $inspectCommand | Out-String | ConvertFrom-Json
    if (-not $report.migrationHistoryCompatible) { throw 'Database migration history is newer or divergent. Use a separate worktree or explicitly reset this disposable environment.' }
    $migrationRole = if ($Context.State.Provisioned) { 'migrator' } else { 'setup' }
    Invoke-DevDatabase $Context $migrationRole @('migrate') | Out-Null
    if (-not $Context.State.Provisioned) {
        Invoke-DevDatabase $Context setup @('principals','provision',
            '--web-user','workbench_web_local','--web-password-file','/run/secrets/web-password',
            '--operator-user','workbench_operator_local','--operator-password-file','/run/secrets/operator-password',
            '--migrator-user','workbench_migrator_local','--migrator-password-file','/run/secrets/migrator-password',
            '--tenant-context-proof-key-file','/run/secrets/tenant-proof') @('web-password','operator-password','migrator-password','tenant-proof') | Out-Null
        $Context.State.Provisioned = $true
        Write-DevState $Context
    }
    if (-not $report.bootstrapCompleted) {
        Invoke-DevDatabase $Context operator @('bootstrap','--tenant-name',$Context.State.TenantName,
            '--admin-email',$Context.State.AdminEmail,'--password-file','/run/secrets/admin-password') @('admin-password') | Out-Null
    }
}
