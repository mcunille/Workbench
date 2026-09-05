[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments)][string[]]$PlaywrightArguments
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker -and $IsWindows) {
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Programs/DockerDesktop/resources/bin/docker.exe'),
        'C:/Program Files/Docker/Docker/resources/bin/docker.exe'
    )) {
        if (Test-Path -LiteralPath $candidate) { $docker = Get-Command $candidate; break }
    }
}
if (-not $docker) { throw 'Docker CLI is required for browser tests.' }

$token = 'browser-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$container = "workbench-sql-$token"
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $temporaryBase $token))
$previousRun = $env:WORKBENCH_BROWSER_RUN
$previousPath = $env:PATH
$exitCode = 1
try {
    $env:WORKBENCH_BROWSER_RUN = $token
    $env:PATH = (Split-Path -Parent $docker.Source) + [IO.Path]::PathSeparator + $env:PATH
    Push-Location (Join-Path $repositoryRoot 'tests/Workbench.BrowserTests')
    try {
        & node './node_modules/@playwright/test/cli.js' test @PlaywrightArguments
        $exitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
}
finally {
    try {
        # Playwright can force-kill its server child, so cleanup belongs to this parent.
        $existing = & $docker.Source ps --all --quiet --filter "name=^/${container}$"
        if ($LASTEXITCODE -ne 0) { throw 'Browser container cleanup lookup failed.' }
        if ($existing) {
            & $docker.Source rm --force $container | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Browser container cleanup failed.' }
        }
        $remaining = & $docker.Source ps --all --quiet --filter "name=^/${container}$"
        if ($LASTEXITCODE -ne 0 -or $remaining) { throw 'Browser SQL container was not removed.' }
        if (Test-Path -LiteralPath $temporaryRoot) {
            if ([IO.Path]::GetFileName($temporaryRoot) -cne $token -or
                [IO.Path]::GetFullPath((Split-Path -Parent $temporaryRoot)).TrimEnd('/','\') -ne
                $temporaryBase.TrimEnd('/','\')) {
                throw 'Refusing to remove an unexpected browser test directory.'
            }
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
        Write-Host 'Browser test SQL container and temporary files removed.'
    }
    finally {
        $env:WORKBENCH_BROWSER_RUN = $previousRun
        $env:PATH = $previousPath
    }
}
exit $exitCode
