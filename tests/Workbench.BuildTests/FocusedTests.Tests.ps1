[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runner = Join-Path $repositoryRoot 'scripts/test-focused.ps1'
$global:focusedCalls = [Collections.Generic.List[object]]::new()
$global:focusedFailureAt = 0

function global:dotnet {
    $global:focusedCalls.Add(@{ Tool = 'dotnet'; Arguments = @($args) })
    $global:LASTEXITCODE = if ($global:focusedCalls.Count -eq $global:focusedFailureAt) { 17 } else { 0 }
}
function global:npm {
    $global:focusedCalls.Add(@{ Tool = 'npm'; Arguments = @($args) })
    $global:LASTEXITCODE = if ($global:focusedCalls.Count -eq $global:focusedFailureAt) { 17 } else { 0 }
}
function Assert-Arguments($Index, $Tool, [string[]]$Expected) {
    $actual = $global:focusedCalls[$Index]
    if ($actual.Tool -ne $Tool -or
        (ConvertTo-Json -Compress @($actual.Arguments)) -cne (ConvertTo-Json -Compress @($Expected))) {
        throw "Unexpected command at index ${Index}: $($actual | ConvertTo-Json -Compress)"
    }
}

try {
    # GIVEN no selection, or an empty/option-shaped selector
    foreach ($selection in @(@{}, @{ ServerFilter = ' ' }, @{ ClientFiles = @() },
        @{ ClientFiles = @('') }, @{ BrowserFiles = @('auth.spec.ts', ' ') },
        @{ ClientFiles = @('--passWithNoTests') }, @{ BrowserFiles = @('--ui') })) {
        $global:focusedCalls.Clear()
        # WHEN the focused runner is invoked
        $rejected = $false
        try { & $runner @selection } catch { $rejected = $true }
        # THEN it rejects the selection before invoking any tool or running an unfiltered suite.
        if (-not $rejected -or $global:focusedCalls.Count -ne 0) { throw 'Invalid selection ran commands.' }
    }

    $serverProject = Join-Path $repositoryRoot 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj'
    $clientRoot = Join-Path $repositoryRoot 'src/Workbench.Client'
    $browserRoot = Join-Path $repositoryRoot 'tests/Workbench.BrowserTests'
    $filter = 'FullyQualifiedName~PhotoProcessorTests|FullyQualifiedName~Other Tests'
    # GIVEN a server filter containing spaces and a Boolean operator
    $global:focusedCalls.Clear()
    # WHEN only server tests are selected
    & $runner -ServerFilter $filter
    # THEN the current source is built, zero matches fail, the filter stays one argument, and npm is never invoked.
    if ($global:focusedCalls.Count -ne 1) { throw 'Server-only selection ran extra commands.' }
    Assert-Arguments 0 dotnet @('test', $serverProject, '--configuration', 'Release', '--no-restore', '--filter', $filter,
        '--', 'RunConfiguration.TreatNoTestsAsError=true')

    # GIVEN two client test selectors including a filename with spaces
    $global:focusedCalls.Clear()
    # WHEN only client tests are selected
    & $runner -ClientFiles @('src/one.test.ts', 'src/with space.test.ts')
    # THEN only the selected tests run without an install, build, or browser startup.
    if ($global:focusedCalls.Count -ne 1) { throw 'Client-only selection ran extra commands.' }
    Assert-Arguments 0 npm @('run', 'test:run', '--prefix', $clientRoot, '--', 'src/one.test.ts', 'src/with space.test.ts')

    # GIVEN browser selectors and dependencies already installed
    $global:focusedCalls.Clear()
    # WHEN browser tests are selected
    & $runner -BrowserFiles @('auth.spec.ts', 'photo.spec.ts')
    # THEN a fresh client build precedes the selected tests through the existing cleanup-owning browser command.
    if ($global:focusedCalls.Count -ne 2) { throw 'Browser-only selection ran extra commands.' }
    Assert-Arguments 0 npm @('run', 'build', '--prefix', $clientRoot)
    Assert-Arguments 1 npm @('test', '--prefix', $browserRoot, '--', 'auth.spec.ts', 'photo.spec.ts')

    # GIVEN only client tests need dependencies
    $global:focusedCalls.Clear()
    # WHEN installation is requested
    & $runner -ClientFiles 'one.test.ts' -InstallDependencies
    # THEN browser dependencies and .NET restore are skipped.
    if ($global:focusedCalls.Count -ne 2) { throw 'Client dependency setup ran unrelated commands.' }
    Assert-Arguments 0 npm @('ci', '--prefix', $clientRoot, '--ignore-scripts', '--no-audit', '--no-fund')

    # GIVEN browser tests need both the client and SQL-backed server
    $global:focusedCalls.Clear()
    # WHEN installation is requested without a server-test selection
    & $runner -BrowserFiles 'auth.spec.ts' -InstallDependencies
    # THEN server packages and both npm dependency sets are prepared before the build.
    if ($global:focusedCalls.Count -ne 5) { throw 'Browser dependency setup omitted or repeated preparation.' }
    Assert-Arguments 0 dotnet @('restore', (Join-Path $repositoryRoot 'Workbench.slnx'), '--locked-mode')
    Assert-Arguments 1 npm @('ci', '--prefix', $clientRoot, '--ignore-scripts', '--no-audit', '--no-fund')
    Assert-Arguments 2 npm @('ci', '--prefix', $browserRoot, '--ignore-scripts', '--no-audit', '--no-fund')

    # GIVEN all three selections and explicit dependency installation
    $global:focusedCalls.Clear()
    # WHEN the runner prepares and executes the selected suites
    & $runner -ServerFilter $filter -ClientFiles 'one.test.ts' -BrowserFiles 'auth.spec.ts' -InstallDependencies
    # THEN locked restores/installations happen once for each needed dependency set.
    if ($global:focusedCalls.Count -ne 7) { throw 'Combined selection repeated or omitted work.' }
    Assert-Arguments 0 dotnet @('restore', (Join-Path $repositoryRoot 'Workbench.slnx'), '--locked-mode')
    Assert-Arguments 1 npm @('ci', '--prefix', $clientRoot, '--ignore-scripts', '--no-audit', '--no-fund')
    Assert-Arguments 2 npm @('ci', '--prefix', $browserRoot, '--ignore-scripts', '--no-audit', '--no-fund')

    # GIVEN a failure at each preparation, build, or test command
    foreach ($failureAt in 1..7) {
        $global:focusedCalls.Clear()
        $global:focusedFailureAt = $failureAt
        $originalLocation = (Get-Location).Path
        $failed = $false
        # WHEN the failing command returns a nonzero exit code
        try { & $runner -ServerFilter $filter -ClientFiles 'one.test.ts' -BrowserFiles 'auth.spec.ts' -InstallDependencies }
        catch { $failed = $_.Exception.Message -match 'failed with exit code 17' }
        # THEN the failure reaches the caller, later commands do not run, and location is restored.
        if (-not $failed -or $global:focusedCalls.Count -ne $failureAt -or (Get-Location).Path -ne $originalLocation) {
            throw "Failure at command $failureAt was swallowed or execution continued."
        }
    }
    Write-Host 'Focused test selection, command forwarding, fresh builds, dependency opt-in, and failure propagation passed.'
}
finally {
    Remove-Item Function:\dotnet, Function:\npm
    Remove-Variable focusedCalls, focusedFailureAt -Scope Global
}
