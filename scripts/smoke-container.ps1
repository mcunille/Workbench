[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker -and $IsWindows) {
    $candidate = 'C:\Program Files\Docker\Docker\resources\bin\docker.exe'
    if (Test-Path -LiteralPath $candidate) { $docker = Get-Item -LiteralPath $candidate }
}
if (-not $docker) { throw 'Docker CLI is required for the container smoke test.' }

$token = "{0}-{1}" -f $PID, [Guid]::NewGuid().ToString('N').Substring(0, 12)
$safeToken = $token.Replace('-', '_')
$image = "workbench-smoke:$token"
$appContainer = "workbench-smoke-$token"
$sqlContainer = "workbench-smoke-sql-$token"
$network = "workbench-smoke-$token"
$database = "workbench_smoke_$safeToken"
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase "workbench-smoke-$token"
$exportArchive = Join-Path $temporaryRoot 'runtime.tar'
$suffix = [Guid]::NewGuid().ToString('N')
$sqlPassword = "SmokeSql-$suffix-Aa9!"
$webPassword = "SmokeWeb-$suffix-Aa9!"
$operatorPassword = "SmokeOperator-$suffix-Aa9!"
$migratorPassword = "SmokeMigrator-$suffix-Aa9!"
$certificatePassword = "SmokeCertificate-$suffix-Aa9!"
$webUser = "workbench_web_$($suffix.Substring(0, 12))"
$operatorUser = "workbench_operator_$($suffix.Substring(0, 12))"
$migratorUser = "workbench_migrator_$($suffix.Substring(0, 12))"

function Assert-NativeCommandSucceeded([string]$name) {
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
}

function Write-SecretFile([string]$name, [string]$value) {
    $path = Join-Path $temporaryRoot $name
    [IO.File]::WriteAllText($path, $value)
    return $path
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
$sqlEnvironment = Write-SecretFile 'sql.env' "ACCEPT_EULA=Y`nMSSQL_SA_PASSWORD=$sqlPassword"
$setupConnection = Write-SecretFile 'setup.connection' "Server=$sqlContainer,1433;Database=$database;User Id=sa;Password=$sqlPassword;Encrypt=True;TrustServerCertificate=True"
$webPasswordFile = Write-SecretFile 'web.password' $webPassword
$operatorPasswordFile = Write-SecretFile 'operator.password' $operatorPassword
$migratorPasswordFile = Write-SecretFile 'migrator.password' $migratorPassword
$tenantContextProofKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$tenantContextProofKeyFile = Write-SecretFile 'tenant-context-proof-key.txt' $tenantContextProofKey
$adminPasswordFile = Write-SecretFile 'admin.password' 'Smoke Correct Horse 8!'
$operatorConnection = Write-SecretFile 'operator.connection' "Server=$sqlContainer,1433;Database=$database;User Id=$operatorUser;Password=$operatorPassword;Encrypt=True;TrustServerCertificate=True"
$certificatePath = Join-Path $temporaryRoot 'data-protection.pfx'
$rsa = [Security.Cryptography.RSA]::Create(2048)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=Workbench Data Protection', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
    [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(1))
[IO.File]::WriteAllBytes(
    $certificatePath,
    $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $certificatePassword))
$certificate.Dispose()
$rsa.Dispose()

try {
    & (Join-Path $PSScriptRoot 'test-compose-proxy.ps1') -DockerPath $docker.Source
    & $docker.Source build --file (Join-Path $repositoryRoot 'Dockerfile') --tag $image $repositoryRoot
    Assert-NativeCommandSucceeded 'docker build'
    $configuredUser = & $docker.Source image inspect --format '{{.Config.User}}' $image
    Assert-NativeCommandSucceeded 'docker image inspect'
    if ($configuredUser.Trim() -ne '1654') { throw "Runtime image user must be 1654; found '$configuredUser'." }

    & $docker.Source network create $network | Out-Null
    Assert-NativeCommandSucceeded 'docker network create'
    & $docker.Source run --detach --name $sqlContainer --network $network --env-file $sqlEnvironment `
        'mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04' | Out-Null
    Assert-NativeCommandSucceeded 'SQL Server start'
    $probe = 'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q "SELECT 1"'
    $ready = $false
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        & $docker.Source exec $sqlContainer /bin/bash -c $probe *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Smoke SQL Server did not become ready.' }
    $create = 'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q "EXEC sp_configure ''contained database authentication'', 1; RECONFIGURE; CREATE DATABASE [{0}]; ALTER DATABASE [{0}] SET CONTAINMENT = PARTIAL"' -f $database
    & $docker.Source exec $sqlContainer /bin/bash -c $create
    Assert-NativeCommandSucceeded 'smoke database create'

    $mount = "${temporaryRoot}:/run/workbench:ro"
    $databaseTool = '/opt/workbench/database/Workbench.Database.dll'
    & $docker.Source run --rm --network $network --volume $mount --entrypoint dotnet $image `
        $databaseTool migrate --connection-file /run/workbench/setup.connection --expected-database $database
    Assert-NativeCommandSucceeded 'containerized migration'
    & $docker.Source run --rm --network $network --volume $mount --entrypoint dotnet $image `
        $databaseTool principals provision --connection-file /run/workbench/setup.connection --expected-database $database `
        --web-user $webUser --web-password-file /run/workbench/web.password `
        --operator-user $operatorUser --operator-password-file /run/workbench/operator.password `
        --migrator-user $migratorUser --migrator-password-file /run/workbench/migrator.password `
        --tenant-context-proof-key-file /run/workbench/tenant-context-proof-key.txt
    Assert-NativeCommandSucceeded 'containerized principal provisioning'
    & $docker.Source run --rm --network $network --volume $mount --entrypoint dotnet $image `
        $databaseTool bootstrap --connection-file /run/workbench/operator.connection --expected-database $database `
        --tenant-name 'Smoke Tenant' --admin-email 'smoke-admin@example.test' `
        --password-file /run/workbench/admin.password
    Assert-NativeCommandSucceeded 'containerized bootstrap'

    $networkGateway = (& $docker.Source network inspect --format '{{(index .IPAM.Config 0).Gateway}}' $network).Trim()
    Assert-NativeCommandSucceeded 'docker network inspect'
    $appEnvironment = Write-SecretFile 'app.env' @"
ASPNETCORE_ENVIRONMENT=Production
WORKBENCH_WEB_CONNECTION=Server=$sqlContainer,1433;Database=$database;User Id=$webUser;Password=$webPassword;Encrypt=True;TrustServerCertificate=True
WORKBENCH_TENANT_CONTEXT_PROOF_KEY_FILE=/run/secrets/tenant-context-proof-key
WORKBENCH_DATA_PROTECTION_CERTIFICATE_PATH=/run/secrets/data-protection.pfx
WORKBENCH_DATA_PROTECTION_CERTIFICATE_PASSWORD=$certificatePassword
WORKBENCH_KNOWN_PROXY=$networkGateway
"@
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $hostPort = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
    & $docker.Source run --detach --name $appContainer --network $network --env-file $appEnvironment `
        --read-only --tmpfs '/tmp:rw,noexec,nosuid,size=64m,uid=1654,gid=1654' `
        --cap-drop ALL --security-opt 'no-new-privileges:true' `
        --volume "${certificatePath}:/run/secrets/data-protection.pfx:ro" `
        --volume "${tenantContextProofKeyFile}:/run/secrets/tenant-context-proof-key:ro" `
        --publish "127.0.0.1:${hostPort}:8080" $image | Out-Null
    Assert-NativeCommandSucceeded 'hardened runtime start'

    $baseUrl = "http://127.0.0.1:$hostPort"
    $healthy = $false
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        try {
            if ((Invoke-WebRequest -Uri "$baseUrl/health/ready" -SkipHttpErrorCheck).StatusCode -eq 200) {
                $healthy = $true; break
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthy) { throw "Runtime did not become ready.`n$(& $docker.Source logs $appContainer 2>&1)" }

    $shell = Invoke-WebRequest -Uri "$baseUrl/client/route" -SkipHttpErrorCheck
    if ($shell.StatusCode -ne 200 -or $shell.Headers.'Content-Type' -notmatch '^text/html') { throw 'React shell failed.' }
    $apiMiss = Invoke-WebRequest -Uri "$baseUrl/api/not-a-route" -SkipHttpErrorCheck
    if ($apiMiss.StatusCode -ne 404 -or $apiMiss.Headers.'Content-Type' -notmatch '^application/problem\+json') { throw 'API miss contract failed.' }
    # GIVEN one client has exhausted its network budget through the trusted ingress peer.
    $attackerHeaders = @{ 'X-Forwarded-For' = '192.0.2.10'; 'X-Forwarded-Proto' = 'https' }
    $attackerAntiforgery = Invoke-WebRequest -Uri "$baseUrl/api/auth/antiforgery" -Headers $attackerHeaders
    $attackerHeaders['X-CSRF-TOKEN'] = ($attackerAntiforgery.Content | ConvertFrom-Json).requestToken
    $attackerHeaders['Cookie'] = ($attackerAntiforgery.Headers.'Set-Cookie' -split ';')[0]
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        $rejected = Invoke-WebRequest -Uri "$baseUrl/api/auth/login" -Method Post `
            -Headers $attackerHeaders -ContentType 'application/json' `
            -Body (@{ email = 'unknown@example.test'; password = 'Invalid Password 8!' } | ConvertTo-Json) `
            -SkipHttpErrorCheck
        if ($rejected.StatusCode -ne 401) { throw 'Expected rejected login while exhausting the client budget.' }
    }
    # THEN even valid credentials are rejected from the exhausted network partition.
    $limitedLogin = Invoke-WebRequest -Uri "$baseUrl/api/auth/login" -Method Post `
        -Headers $attackerHeaders -ContentType 'application/json' `
        -Body (@{ email = 'smoke-admin@example.test'; password = 'Smoke Correct Horse 8!' } | ConvertTo-Json) `
        -SkipHttpErrorCheck
    if ($limitedLogin.StatusCode -ne 401) { throw 'Exhausted client network budget allowed valid credentials.' }
    # WHEN another forwarded client signs in with valid credentials.
    $forwardedHeaders = @{ 'X-Forwarded-For' = '192.0.2.20'; 'X-Forwarded-Proto' = 'https' }
    $antiforgeryResponse = Invoke-WebRequest -Uri "$baseUrl/api/auth/antiforgery" -Headers $forwardedHeaders
    $antiforgery = $antiforgeryResponse.Content | ConvertFrom-Json
    $antiforgeryCookie = ($antiforgeryResponse.Headers.'Set-Cookie' -split ';')[0]
    $loginHeaders = $forwardedHeaders.Clone()
    $loginHeaders['X-CSRF-TOKEN'] = $antiforgery.requestToken
    $loginHeaders['Cookie'] = $antiforgeryCookie
    $login = Invoke-WebRequest -Uri "$baseUrl/api/auth/login" -Method Post `
        -Headers $loginHeaders -ContentType 'application/json' `
        -Body (@{ email = 'smoke-admin@example.test'; password = 'Smoke Correct Horse 8!' } | ConvertTo-Json) `
        -SkipHttpErrorCheck
    # THEN the first client has not exhausted this client's network partition.
    if ($login.StatusCode -ne 204) { throw 'Container authentication failed after a different client exhausted its budget.' }
    $sessionCookie = (($login.Headers.'Set-Cookie' | Where-Object { $_ -match '__Host-Workbench.Session=' }) -split ';')[0]
    $identityHeaders = $forwardedHeaders.Clone()
    $identityHeaders['Cookie'] = $sessionCookie
    if ((Invoke-WebRequest -Uri "$baseUrl/api/auth/me" -Headers $identityHeaders -SkipHttpErrorCheck).StatusCode -ne 200) {
        throw 'Container durable session validation failed.'
    }

    $runtimeEnvironment = & $docker.Source inspect --format '{{json .Config.Env}}' $appContainer
    if ($runtimeEnvironment -match [regex]::Escape($sqlPassword) -or
        $runtimeEnvironment -match [regex]::Escape($operatorPassword) -or
        $runtimeEnvironment -match [regex]::Escape($migratorPassword) -or
        $runtimeEnvironment -match [regex]::Escape($tenantContextProofKey)) {
        throw 'Runtime container environment received setup, operator, migration, or tenant-proof secrets.'
    }
    & $docker.Source export --output $exportArchive $appContainer
    Assert-NativeCommandSucceeded 'docker export'
    $forbidden = (tar -tf $exportArchive) | Where-Object {
        $_ -match '(^|/)(node|npm|node_modules|src|tests)(/|$)' -or $_ -match '\.(cs|tsx|ts)$'
    }
    if ($forbidden) { throw "Runtime image contains source or Node build content: $($forbidden -join ', ')" }
    Write-Host "Hardened SQL-backed runtime verified at $baseUrl as user $configuredUser."
}
catch {
    $runningApp = & $docker.Source ps --all --quiet --filter "name=^/${appContainer}$" 2>$null
    if ($runningApp) {
        Write-Error "Runtime logs:`n$(& $docker.Source logs $appContainer 2>&1)"
    }
    throw
}
finally {
    foreach ($container in @($appContainer, $sqlContainer)) {
        $existing = & $docker.Source ps --all --quiet --filter "name=^/${container}$" 2>$null
        if ($existing) { & $docker.Source rm --force $container | Out-Null }
    }
    $existingNetwork = & $docker.Source network ls --quiet --filter "name=^${network}$" 2>$null
    if ($existingNetwork) { & $docker.Source network rm $network | Out-Null }
    $existingImage = & $docker.Source image ls --quiet $image 2>$null
    if ($existingImage) { & $docker.Source image rm --force $image | Out-Null }
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolved = [IO.Path]::GetFullPath($temporaryRoot)
        if (-not $resolved.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolved).StartsWith('workbench-smoke-', [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected smoke path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
