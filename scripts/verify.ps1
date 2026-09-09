[CmdletBinding()]
param(
    [switch]$SkipDependencyInstall,
    [ValidateRange(2, 4)][int]$ServerPartitions = 2,
    [ValidateRange(1, 4)][int]$ServerConcurrency = 2
)

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

. (Join-Path $PSScriptRoot 'verification-stages.ps1')
. (Join-Path $PSScriptRoot 'verification-artifacts.ps1')
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $repositoryRoot "artifacts/verification/$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$stages = @()
$stagesJoined = $false
$gateClock = [Diagnostics.Stopwatch]::StartNew()
$prerequisiteClock = [Diagnostics.Stopwatch]::StartNew()
$passed = $false
$priorManifest = $env:WORKBENCH_VERIFICATION_MANIFEST
$priorRun = $env:WORKBENCH_VERIFICATION_RUN
Push-Location $repositoryRoot
try {
    Assert-ToolVersion 'dotnet' '10.0.401' { dotnet --version }
    Assert-ToolVersion 'Node.js' 'v26.7.0' { node --version }
    Assert-ToolVersion 'npm' '11.19.0' { npm --version }
    Assert-DocumentationCurrent
    dotnet restore Workbench.slnx --locked-mode
    Assert-NativeCommandSucceeded 'dotnet restore --locked-mode'
    if (-not $SkipDependencyInstall) {
        npm ci --prefix $clientRoot --ignore-scripts --no-audit --no-fund
        Assert-NativeCommandSucceeded 'npm ci'
        npm ci --prefix $browserRoot --ignore-scripts --no-audit --no-fund
        Assert-NativeCommandSucceeded 'browser npm ci'
    }
    # This process only reads client inputs; output-producing builds remain serialized below.
    $stages += Start-VerificationStage -Name client -RepositoryRoot $repositoryRoot -Arguments @($clientRoot) -Action {
        param($client)
        npm run lint --prefix $client
        if ($LASTEXITCODE -ne 0) { throw 'Client lint failed.' }
        npm run test:run --prefix $client
        if ($LASTEXITCODE -ne 0) { throw 'Client tests failed.' }
    }
    dotnet format Workbench.slnx --verify-no-changes --no-restore
    Assert-NativeCommandSucceeded 'dotnet format'
    $receipt = New-VerificationBuildReceipt -RepositoryRoot $repositoryRoot -RunId $runId
    dotnet build Workbench.slnx --configuration Release --no-restore `
        -p:UseAppHost=false -p:BuildClient=false "-p:OpenApiDocumentsDirectory=$openApiRoot"
    Assert-NativeCommandSucceeded 'Release build and OpenAPI document generation'
    # Let early readers finish before rewriting generated TypeScript, even on a fast warm build.
    $stages[0].Job | Wait-Job | Out-Null
    npm run generate:api --prefix $clientRoot
    Assert-NativeCommandSucceeded 'TypeScript API generation'
    git diff --exit-code -- src/Workbench.Client/openapi/Workbench.Server.json src/Workbench.Client/src/api/generated.ts
    Assert-NativeCommandSucceeded 'generated API drift check'

    # Each child owns a SQL container; process-wide pool and image state never cross partitions.
    $stages += Start-VerificationStage -Name server -RepositoryRoot $repositoryRoot `
        -Arguments @($ServerPartitions, $ServerConcurrency, (Join-Path $runRoot 'server-tests')) -Action {
        param($count, $concurrency, $results)
        & ./scripts/test-server-partitions.ps1 -PartitionCount $count -MaxConcurrency $concurrency -NoBuild -ResultsDirectory $results
        if (-not $?) { throw 'Server partitions failed.' }
    }
    npm run typecheck --prefix $clientRoot
    Assert-NativeCommandSucceeded 'client typecheck'
    npm run build --prefix $clientRoot
    Assert-NativeCommandSucceeded 'client build'
    $manifest = Publish-VerificationArtifacts -RepositoryRoot $repositoryRoot -RunId $runId -BuildReceipt $receipt -OutputRoot $runRoot
    $env:WORKBENCH_VERIFICATION_MANIFEST = $manifest
    $env:WORKBENCH_VERIFICATION_RUN = $runId
    $prerequisiteClock.Stop()
    $stages += Start-VerificationStage -Name browser -RepositoryRoot $repositoryRoot -Arguments @($browserRoot) -Action {
        param($browser)
        npm test --prefix $browser
        if ($LASTEXITCODE -ne 0) { throw 'Browser tests failed.' }
    }
    $stages += Start-VerificationStage -Name published -RepositoryRoot $repositoryRoot -Action {
        & ./scripts/test-publish.ps1
        if (-not $?) { throw 'Published release-unit verification failed.' }
    }
    $stagesJoined = $true
    Complete-VerificationStages -Stages $stages -RequiredNames @('client', 'server', 'browser', 'published') `
        -TimingPath (Join-Path $runRoot 'stages.json')
    $passed = $true
    Write-Host 'Workbench source and published release-unit verification passed.'
}
finally {
    # Even a prerequisite failure must join started stages and retain their results.
    try {
        if (-not $stagesJoined -and $stages.Count) {
            Complete-VerificationStages -Stages $stages -RequiredNames @($stages | ForEach-Object Name) `
                -TimingPath (Join-Path $runRoot 'stages.json')
        }
    }
    finally {
        foreach ($stage in $stages) { Remove-Job -Job $stage.Job -Force -ErrorAction SilentlyContinue }
        $env:WORKBENCH_VERIFICATION_MANIFEST = $priorManifest
        $env:WORKBENCH_VERIFICATION_RUN = $priorRun
        [pscustomobject]@{ RunId = $runId; Succeeded = $passed; Seconds = $gateClock.Elapsed.TotalSeconds;
            PrerequisiteSeconds = $prerequisiteClock.Elapsed.TotalSeconds; ServerPartitions = $ServerPartitions;
            ServerConcurrency = $ServerConcurrency; ProcessorCount = [Environment]::ProcessorCount } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'gate.json')
        Write-Host ("Gate evidence: {0}; elapsed {1:N2}s" -f $runRoot, $gateClock.Elapsed.TotalSeconds)
        Pop-Location
    }
}
