[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker -and $IsWindows) {
    $candidate = 'C:\Program Files\Docker\Docker\resources\bin\docker.exe'
    if (Test-Path -LiteralPath $candidate) { $docker = Get-Item -LiteralPath $candidate }
}
if (-not $docker) { throw 'Docker CLI is required for browser tests.' }

$token = $env:WORKBENCH_BROWSER_RUN
if ($token -notmatch '^browser-[a-f0-9]{12}$') {
    throw 'Start browser tests through npm test so the parent process owns cleanup.'
}
$database = "workbench_$($token.Replace('-', '_'))"
$container = "workbench-sql-$token"
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $token))
$publishRoot = Join-Path $temporaryRoot 'publish'
$environmentFile = Join-Path $temporaryRoot 'sql.env'
$setupConnectionFile = Join-Path $temporaryRoot 'setup.connection'
$operatorConnectionFile = Join-Path $temporaryRoot 'operator.connection'
$adminPasswordFile = Join-Path $temporaryRoot 'admin.password'
$webPasswordFile = Join-Path $temporaryRoot 'web.password'
$operatorPasswordFile = Join-Path $temporaryRoot 'operator.password'
$migratorPasswordFile = Join-Path $temporaryRoot 'migrator.password'
$tenantContextProofKeyFile = Join-Path $temporaryRoot 'tenant-context-proof-key.txt'
$suffix = [Guid]::NewGuid().ToString('N')
$sqlPassword = "BrowserSql-$suffix-Aa9!"
$webPassword = "BrowserWeb-$suffix-Aa9!"
$operatorPassword = "BrowserOperator-$suffix-Aa9!"
$migratorPassword = "BrowserMigrator-$suffix-Aa9!"
$webUser = "workbench_web_$($suffix.Substring(0, 12))"
$operatorUser = "workbench_operator_$($suffix.Substring(0, 12))"
$migratorUser = "workbench_migrator_$($suffix.Substring(0, 12))"

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$sqlPort = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
Set-Content -LiteralPath $environmentFile -Value @('ACCEPT_EULA=Y', "MSSQL_SA_PASSWORD=$sqlPassword")
Set-Content -LiteralPath $adminPasswordFile -Value 'Browser Correct Horse 9!'
Set-Content -LiteralPath $webPasswordFile -Value $webPassword
Set-Content -LiteralPath $operatorPasswordFile -Value $operatorPassword
Set-Content -LiteralPath $migratorPasswordFile -Value $migratorPassword
$tenantContextProofKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
Set-Content -LiteralPath $tenantContextProofKeyFile -Value $tenantContextProofKey
$setupConnection = "Server=127.0.0.1,$sqlPort;Database=$database;User Id=sa;Password=$sqlPassword;Encrypt=True;TrustServerCertificate=True"
Set-Content -LiteralPath $setupConnectionFile -Value $setupConnection

function Assert-CommandSucceeded([string]$name) {
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
}

try {
    & $docker.Source run --detach --name $container --env-file $environmentFile `
        --label 'workbench.purpose=browser-test' --label "workbench.run=$token" `
        --publish "127.0.0.1:${sqlPort}:1433" `
        'mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04' | Out-Null
    Assert-CommandSucceeded 'SQL Server container start'

    $ready = $false
    $probeCommand = 'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q "SELECT 1"'
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        & $docker.Source exec $container /bin/bash -c $probeCommand *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Disposable SQL Server did not become ready.' }

    $createCommand = 'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q "EXEC sp_configure ''contained database authentication'', 1; RECONFIGURE; CREATE DATABASE [{0}]; ALTER DATABASE [{0}] SET CONTAINMENT = PARTIAL"' -f $database
    & $docker.Source exec $container /bin/bash -c $createCommand
    Assert-CommandSucceeded 'Disposable database creation'

    dotnet run --project (Join-Path $repositoryRoot 'src/Workbench.Database/Workbench.Database.csproj') -- `
        migrate --connection-file $setupConnectionFile --expected-database $database
    Assert-CommandSucceeded 'Browser database migration'
    dotnet run --project (Join-Path $repositoryRoot 'src/Workbench.Database/Workbench.Database.csproj') -- `
        principals provision --connection-file $setupConnectionFile --expected-database $database `
        --web-user $webUser --web-password-file $webPasswordFile `
        --operator-user $operatorUser --operator-password-file $operatorPasswordFile `
        --migrator-user $migratorUser --migrator-password-file $migratorPasswordFile `
        --tenant-context-proof-key-file $tenantContextProofKeyFile
    Assert-CommandSucceeded 'Browser database principal provisioning'

    $operatorConnection = "Server=127.0.0.1,$sqlPort;Database=$database;User Id=$operatorUser;Password=$operatorPassword;Encrypt=True;TrustServerCertificate=True"
    Set-Content -LiteralPath $operatorConnectionFile -Value $operatorConnection
    dotnet run --project (Join-Path $repositoryRoot 'src/Workbench.Database/Workbench.Database.csproj') -- `
        bootstrap --connection-file $operatorConnectionFile --expected-database $database `
        --tenant-name 'Browser Tenant' --admin-email 'browser-admin@example.test' `
        --password-file $adminPasswordFile
    Assert-CommandSucceeded 'Browser database bootstrap'

    dotnet publish (Join-Path $repositoryRoot 'src/Workbench.Server/Workbench.Server.csproj') `
        --configuration Release --output $publishRoot -p:UseAppHost=false -p:BuildClient=false
    Assert-CommandSucceeded 'Browser application publish'

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:4179'
    $env:ASPNETCORE_CONTENTROOT = $publishRoot
    $env:WORKBENCH_WEB_CONNECTION = "Server=127.0.0.1,$sqlPort;Database=$database;User Id=$webUser;Password=$webPassword;Encrypt=True;TrustServerCertificate=True"
    $env:WORKBENCH_TENANT_CONTEXT_PROOF_KEY = $tenantContextProofKey
    & dotnet (Join-Path $publishRoot 'Workbench.Server.dll')
}
finally {
    $existing = & $docker.Source ps --all --quiet --filter "name=^/${container}$" 2>$null
    if ($existing) { & $docker.Source rm --force $container | Out-Null }
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolved = [IO.Path]::GetFullPath($temporaryRoot)
        $isExpected = [IO.Path]::GetFileName($resolved).StartsWith('browser-', [StringComparison]::Ordinal)
        $isTemporary = $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)
        if (-not $isExpected -or -not $isTemporary) { throw "Refusing to remove unexpected browser test path: $resolved" }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
