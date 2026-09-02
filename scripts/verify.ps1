[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$clientRoot = Join-Path $repositoryRoot 'src/Workbench.Client'
$browserRoot = Join-Path $repositoryRoot 'tests/Workbench.BrowserTests'
$serverProject = Join-Path $repositoryRoot 'src/Workbench.Server/Workbench.Server.csproj'
$openApiRoot = Join-Path $clientRoot 'openapi'

function Assert-NativeCommandSucceeded {
    param([Parameter(Mandatory)][string]$CommandName)

    if ($LASTEXITCODE -ne 0) {
        throw "$CommandName failed with exit code $LASTEXITCODE."
    }
}

function Assert-ToolVersion {
    param(
        [Parameter(Mandatory)][string]$CommandName,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][scriptblock]$ReadVersion
    )

    $actualVersion = (& $ReadVersion).Trim()
    Assert-NativeCommandSucceeded "$CommandName version check"

    if ($actualVersion -ne $ExpectedVersion) {
        throw "$CommandName $ExpectedVersion is required; found $actualVersion."
    }
}

function Assert-DocumentationCurrent {
    $requiredContent = @(
        @{ Path = 'docs/ARCHITECTURE.md'; Text = '**Status:** Implemented' },
        @{ Path = 'docs/specs/2026-09-01-data-identity-tenancy.md'; Text = '**Status:** Implemented' },
        @{ Path = 'docs/operations/database-migrations.md'; Text = './scripts/verify-migrations.ps1 -Scenario Clean' },
        @{ Path = 'docs/operations/database-backup-restore.md'; Text = './scripts/restore-database.ps1' },
        @{ Path = 'README.md'; Text = '.env.dev' },
        @{
            Path = 'docs/ARCHITECTURE.md'
            Text = 'Public recovery remains disabled until'
        }
    )

    foreach ($requirement in $requiredContent) {
        $path = Join-Path $repositoryRoot $requirement.Path
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required documentation file '$($requirement.Path)' is missing."
        }

        $content = Get-Content -LiteralPath $path -Raw
        if (-not $content.Contains($requirement.Text, [StringComparison]::Ordinal)) {
            throw "Documentation '$($requirement.Path)' is missing required text: $($requirement.Text)"
        }
    }
}

Push-Location $repositoryRoot
try {
    Assert-ToolVersion 'dotnet' '10.0.400' { dotnet --version }
    Assert-ToolVersion 'Node.js' 'v26.7.0' { node --version }
    Assert-ToolVersion 'npm' '11.19.0' { npm --version }

    Assert-DocumentationCurrent

    dotnet restore Workbench.slnx --locked-mode
    Assert-NativeCommandSucceeded 'dotnet restore --locked-mode'

    npm ci --prefix $clientRoot
    Assert-NativeCommandSucceeded 'npm ci'

    npm ci --prefix $browserRoot
    Assert-NativeCommandSucceeded 'browser npm ci'

    dotnet format Workbench.slnx --verify-no-changes --no-restore
    Assert-NativeCommandSucceeded 'dotnet format'

    dotnet build $serverProject `
        --no-restore `
        "-p:OpenApiDocumentsDirectory=$openApiRoot"
    Assert-NativeCommandSucceeded 'OpenAPI document generation'

    npm run generate:api --prefix $clientRoot
    Assert-NativeCommandSucceeded 'TypeScript API generation'

    git diff --exit-code -- `
        src/Workbench.Client/openapi/Workbench.Server.json `
        src/Workbench.Client/src/api/generated.ts
    Assert-NativeCommandSucceeded 'generated API drift check'

    dotnet build Workbench.slnx --configuration Release --no-restore
    Assert-NativeCommandSucceeded 'dotnet build'

    dotnet test Workbench.slnx --configuration Release --no-build --no-restore
    Assert-NativeCommandSucceeded 'dotnet test'

    npm run lint --prefix $clientRoot
    Assert-NativeCommandSucceeded 'client lint'

    npm run typecheck --prefix $clientRoot
    Assert-NativeCommandSucceeded 'client typecheck'

    npm run test:run --prefix $clientRoot
    Assert-NativeCommandSucceeded 'client tests'

    npm run build --prefix $clientRoot
    Assert-NativeCommandSucceeded 'client build'

    foreach ($scenario in @('Clean', 'Upgrade', 'ReversibleRollback', 'RestoreRollback')) {
        & (Join-Path $PSScriptRoot 'verify-migrations.ps1') -Scenario $scenario
        if (-not $?) { throw "Migration scenario '$scenario' failed." }
    }

    npm test --prefix $browserRoot
    Assert-NativeCommandSucceeded 'browser tests'

    & (Join-Path $PSScriptRoot 'test-publish.ps1')
    if (-not $?) {
        throw 'Published release-unit verification failed.'
    }

    Write-Host 'Workbench source and published release-unit verification passed.'
}
finally {
    Pop-Location
}
