[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dockerCommand = Get-Command docker -ErrorAction SilentlyContinue

if (-not $dockerCommand) {
    throw 'Docker CLI is required for the container smoke test.'
}

$token = "{0}-{1}" -f $PID, [Guid]::NewGuid().ToString('N').Substring(0, 12)
$imageName = "workbench-smoke:$token"
$containerName = "workbench-smoke-$token"
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$exportArchive = Join-Path $temporaryRoot "workbench-smoke-$token.tar"

function Assert-NativeCommandSucceeded {
    param([Parameter(Mandatory)][string]$CommandName)

    if ($LASTEXITCODE -ne 0) {
        throw "$CommandName failed with exit code $LASTEXITCODE."
    }
}

try {
    docker compose --file (Join-Path $repositoryRoot 'compose.yaml') config --quiet
    Assert-NativeCommandSucceeded 'docker compose config'

    docker build --file (Join-Path $repositoryRoot 'Dockerfile') --tag $imageName $repositoryRoot
    Assert-NativeCommandSucceeded 'docker build'

    $configuredUser = docker image inspect --format '{{.Config.User}}' $imageName
    Assert-NativeCommandSucceeded 'docker image inspect'
    if ($configuredUser.Trim() -ne '1654') {
        throw "Runtime image user must be numeric user 1654; found '$configuredUser'."
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $hostPort = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()

    docker run `
        --detach `
        --name $containerName `
        --read-only `
        --tmpfs '/tmp:rw,noexec,nosuid,size=64m,uid=1654,gid=1654' `
        --cap-drop ALL `
        --security-opt 'no-new-privileges:true' `
        --publish "127.0.0.1:${hostPort}:8080" `
        $imageName | Out-Null
    Assert-NativeCommandSucceeded 'docker run'

    $healthy = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $healthStatus = docker inspect --format '{{.State.Health.Status}}' $containerName
        Assert-NativeCommandSucceeded 'docker inspect'

        if ($healthStatus.Trim() -eq 'healthy') {
            $healthy = $true
            break
        }

        if ($healthStatus.Trim() -eq 'unhealthy') {
            $logs = docker logs $containerName 2>&1
            throw "Container became unhealthy. Logs:`n$logs"
        }

        Start-Sleep -Milliseconds 500
    }

    if (-not $healthy) {
        throw 'Container did not become healthy within 30 seconds.'
    }

    $baseUrl = "http://127.0.0.1:$hostPort"
    $shell = Invoke-WebRequest -Uri "$baseUrl/client/route" -SkipHttpErrorCheck
    if ($shell.StatusCode -ne 200 -or $shell.Headers.'Content-Type' -notmatch '^text/html') {
        throw 'Container did not serve the React shell.'
    }

    $system = Invoke-WebRequest -Uri "$baseUrl/api/system" -SkipHttpErrorCheck
    $systemBody = $system.Content | ConvertFrom-Json
    if ($system.StatusCode -ne 200 -or $systemBody.name -ne 'Workbench') {
        throw 'Container system API contract failed.'
    }

    $apiMiss = Invoke-WebRequest -Uri "$baseUrl/api/not-a-route" -SkipHttpErrorCheck
    if ($apiMiss.StatusCode -ne 404 -or $apiMiss.Headers.'Content-Type' -notmatch '^application/problem\+json') {
        throw 'Container API miss returned the wrong contract.'
    }

    foreach ($healthPath in @('/health/live', '/health/ready')) {
        $health = Invoke-WebRequest -Uri "$baseUrl$healthPath" -SkipHttpErrorCheck
        if ($health.StatusCode -ne 200 -or $health.Headers.'Content-Type' -notmatch '^application/json') {
            throw "Container health contract failed at $healthPath."
        }
    }

    docker export --output $exportArchive $containerName
    Assert-NativeCommandSucceeded 'docker export'
    $archiveEntries = tar -tf $exportArchive
    Assert-NativeCommandSucceeded 'tar -tf'

    $forbiddenEntries = $archiveEntries | Where-Object {
        $_ -match '(^|/)(node|node\.exe|npm|npm\.cmd)$' -or
        $_ -match '(^|/)(src|tests|node_modules)(/|$)' -or
        $_ -match '\.(cs|tsx|ts)$'
    }
    if ($forbiddenEntries) {
        throw "Runtime image contains source or Node.js build content: $($forbiddenEntries -join ', ')"
    }

    Write-Host "Container release unit verified at $baseUrl as user $configuredUser."
}
finally {
    $existingContainer = docker ps --all --quiet --filter "name=^/${containerName}$" 2>$null
    if ($existingContainer) {
        docker rm --force $containerName | Out-Null
    }

    $existingImage = docker image ls --quiet $imageName 2>$null
    if ($existingImage) {
        docker image rm --force $imageName | Out-Null
    }

    if (Test-Path -LiteralPath $exportArchive) {
        $resolvedArchive = [IO.Path]::GetFullPath($exportArchive)
        $isTaskArchive = [IO.Path]::GetFileName($resolvedArchive).StartsWith(
            'workbench-smoke-',
            [StringComparison]::Ordinal)
        $isUnderTemporaryRoot = $resolvedArchive.StartsWith(
            $temporaryRoot,
            [StringComparison]::OrdinalIgnoreCase)

        if (-not $isTaskArchive -or -not $isUnderTemporaryRoot) {
            throw "Refusing to remove unexpected archive path: $resolvedArchive"
        }

        Remove-Item -LiteralPath $resolvedArchive -Force
    }
}
