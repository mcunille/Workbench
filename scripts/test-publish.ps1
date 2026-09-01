[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$publishRoot = Join-Path $temporaryRoot ("workbench-publish-{0}" -f [Guid]::NewGuid().ToString('N'))
$publishedProcess = $null

function Assert-NativeCommandSucceeded {
    param([Parameter(Mandatory)][string]$CommandName)

    if ($LASTEXITCODE -ne 0) {
        throw "$CommandName failed with exit code $LASTEXITCODE."
    }
}

try {
    dotnet publish (Join-Path $repositoryRoot 'src/Workbench.Server/Workbench.Server.csproj') `
        --configuration Release `
        --no-restore `
        --output $publishRoot
    Assert-NativeCommandSucceeded 'dotnet publish'

    $serverAssembly = Join-Path $publishRoot 'Workbench.Server.dll'
    $clientIndex = Join-Path $publishRoot 'wwwroot/index.html'
    $clientAssets = Join-Path $publishRoot 'wwwroot/assets'

    if (-not (Test-Path -LiteralPath $serverAssembly -PathType Leaf)) {
        throw "Published server assembly is missing: $serverAssembly"
    }

    if (-not (Test-Path -LiteralPath $clientIndex -PathType Leaf)) {
        throw "Published client shell is missing: $clientIndex"
    }

    if (-not (Test-Path -LiteralPath $clientAssets -PathType Container) -or
        -not (Get-ChildItem -LiteralPath $clientAssets -File | Select-Object -First 1)) {
        throw "Published client assets are missing: $clientAssets"
    }

    $forbiddenFiles = Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Where-Object {
        $_.Extension -in @('.cs', '.tsx', '.ts') -or $_.Name -in @('node', 'node.exe', 'npm', 'npm.cmd')
    }
    if ($forbiddenFiles) {
        throw "Published output contains forbidden source or build tools: $($forbiddenFiles.FullName -join ', ')"
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $baseUrl = "http://127.0.0.1:$port"

    $publishedProcessParameters = @{
        FilePath = 'dotnet'
        ArgumentList = @($serverAssembly)
        WorkingDirectory = $publishRoot
        Environment = @{
            ASPNETCORE_ENVIRONMENT = 'Production'
            ASPNETCORE_URLS = $baseUrl
        }
        PassThru = $true
    }
    if ($IsWindows) {
        $publishedProcessParameters.WindowStyle = 'Hidden'
    }
    $publishedProcess = Start-Process @publishedProcessParameters

    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($publishedProcess.HasExited) {
            throw "Published server exited before becoming ready with code $($publishedProcess.ExitCode)."
        }

        try {
            $readiness = Invoke-WebRequest -Uri "$baseUrl/health/ready" -SkipHttpErrorCheck
            if ($readiness.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
            # The listener may not be bound during the first few bounded attempts.
        }

        Start-Sleep -Milliseconds 250
    }

    if (-not $ready) {
        throw "Published server did not become ready at $baseUrl."
    }

    $probeProcessParameters = @{
        FilePath = 'dotnet'
        ArgumentList = @($serverAssembly, '--health-check')
        WorkingDirectory = $publishRoot
        Environment = @{ WORKBENCH_HEALTH_URL = "$baseUrl/health/ready" }
        Wait = $true
        PassThru = $true
    }
    if ($IsWindows) {
        $probeProcessParameters.WindowStyle = 'Hidden'
    }
    $probeProcess = Start-Process @probeProcessParameters
    if ($probeProcess.ExitCode -ne 0) {
        throw "Published shell-free health probe failed with exit code $($probeProcess.ExitCode)."
    }

    $shell = Invoke-WebRequest -Uri "$baseUrl/client/route" -SkipHttpErrorCheck
    if ($shell.StatusCode -ne 200 -or $shell.Headers.'Content-Type' -notmatch '^text/html') {
        throw "Published SPA shell contract failed at $baseUrl/client/route."
    }

    $system = Invoke-WebRequest -Uri "$baseUrl/api/system" -SkipHttpErrorCheck
    $systemBody = $system.Content | ConvertFrom-Json
    if ($system.StatusCode -ne 200 -or $systemBody.name -ne 'Workbench' -or
        [string]::IsNullOrWhiteSpace($systemBody.version)) {
        throw "Published system API contract failed at $baseUrl/api/system."
    }

    $apiMiss = Invoke-WebRequest -Uri "$baseUrl/api/not-a-route" -SkipHttpErrorCheck
    if ($apiMiss.StatusCode -ne 404 -or $apiMiss.Headers.'Content-Type' -notmatch '^application/problem\+json') {
        throw "Published API miss contract failed at $baseUrl/api/not-a-route."
    }

    Write-Host "Published release unit verified at $baseUrl."
}
finally {
    if ($publishedProcess -and -not $publishedProcess.HasExited) {
        Stop-Process -Id $publishedProcess.Id
        $publishedProcess.WaitForExit()
    }

    $resolvedPublishRoot = [IO.Path]::GetFullPath($publishRoot)
    $isTaskDirectory = [IO.Path]::GetFileName($resolvedPublishRoot).StartsWith(
        'workbench-publish-',
        [StringComparison]::Ordinal)
    $isUnderTemporaryRoot = $resolvedPublishRoot.StartsWith(
        $temporaryRoot,
        [StringComparison]::OrdinalIgnoreCase)

    if (Test-Path -LiteralPath $resolvedPublishRoot) {
        if (-not $isTaskDirectory -or -not $isUnderTemporaryRoot) {
            throw "Refusing to remove unexpected publish path: $resolvedPublishRoot"
        }

        Remove-Item -LiteralPath $resolvedPublishRoot -Recurse -Force
    }
}
