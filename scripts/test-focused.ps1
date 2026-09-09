[CmdletBinding()]
param(
    [string]$ServerFilter,
    [string[]]$ClientFiles,
    [string[]]$BrowserFiles,
    [switch]$InstallDependencies
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$clientRoot = Join-Path $repositoryRoot 'src/Workbench.Client'
$browserRoot = Join-Path $repositoryRoot 'tests/Workbench.BrowserTests'
$serverProject = Join-Path $repositoryRoot 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj'

if ($PSBoundParameters.ContainsKey('ServerFilter') -and [string]::IsNullOrWhiteSpace($ServerFilter)) {
    throw 'ServerFilter must be a nonempty dotnet test filter.'
}
foreach ($name in @('ClientFiles', 'BrowserFiles')) {
    if (-not $PSBoundParameters.ContainsKey($name)) { continue }
    $selectors = @($PSBoundParameters[$name])
    if ($selectors.Count -eq 0) { throw "$name must contain at least one test file selector." }
    foreach ($selector in $selectors) {
        if ([string]::IsNullOrWhiteSpace($selector) -or $selector.TrimStart().StartsWith('-')) {
            throw "$name must contain nonempty test file selectors, not command options."
        }
    }
}
if (-not $ServerFilter -and -not $ClientFiles -and -not $BrowserFiles) {
    throw 'Select tests with -ServerFilter, -ClientFiles, or -BrowserFiles. Use verify.ps1 for full verification.'
}

function Assert-NativeCommandSucceeded {
    param([string]$CommandName)
    if ($LASTEXITCODE -ne 0) { throw "$CommandName failed with exit code $LASTEXITCODE." }
}

Push-Location $repositoryRoot
try {
    if ($InstallDependencies) {
        if ($ServerFilter -or $BrowserFiles) {
            dotnet restore (Join-Path $repositoryRoot 'Workbench.slnx') --locked-mode
            Assert-NativeCommandSucceeded 'dotnet restore'
        }
        if ($ClientFiles -or $BrowserFiles) {
            npm ci --prefix $clientRoot --ignore-scripts --no-audit --no-fund
            Assert-NativeCommandSucceeded 'client npm ci'
        }
        if ($BrowserFiles) {
            npm ci --prefix $browserRoot --ignore-scripts --no-audit --no-fund
            Assert-NativeCommandSucceeded 'browser npm ci'
        }
    }

    if ($ServerFilter) {
        # dotnet test builds current source; never reuse an unverified --no-build output.
        dotnet test $serverProject --configuration Release --no-restore --filter $ServerFilter `
            '--' RunConfiguration.TreatNoTestsAsError=true
        Assert-NativeCommandSucceeded 'server tests'
    }
    if ($ClientFiles) {
        npm run test:run --prefix $clientRoot '--' @ClientFiles
        Assert-NativeCommandSucceeded 'client tests'
    }
    if ($BrowserFiles) {
        npm run build --prefix $clientRoot
        Assert-NativeCommandSucceeded 'client build'
        npm test --prefix $browserRoot '--' @BrowserFiles
        Assert-NativeCommandSucceeded 'browser tests'
    }
    Write-Host 'Selected tests passed. Run verify.ps1 and smoke-container.ps1 before delivery.'
}
finally { Pop-Location }
