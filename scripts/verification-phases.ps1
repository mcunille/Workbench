. (Join-Path $PSScriptRoot 'verification-stages.ps1')
. (Join-Path $PSScriptRoot 'verification-artifacts.ps1')

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
        @{ Path = 'docs/operations/database-migrations.md'; Text = './scripts/verify-migrations.ps1 -Scenario Clean' },
        @{ Path = 'docs/operations/database-backup-restore.md'; Text = './scripts/restore-database.ps1' },
        @{ Path = 'README.md'; Text = './scripts/dev-up.ps1' },
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

function Invoke-VerificationPreparation {
    param([string]$RepositoryRoot, [string]$RunId, [string]$RunRoot,
        [switch]$SkipDependencyInstall, [scriptblock]$OnServerReady = {})
    $repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    $clientRoot = Join-Path $repositoryRoot 'src/Workbench.Client'
    $browserRoot = Join-Path $repositoryRoot 'tests/Workbench.BrowserTests'
    $openApiRoot = Join-Path $clientRoot 'openapi'
    $clientStage = $null
    $joined = $false
    New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null
    Push-Location $repositoryRoot
    try {
        Assert-ToolVersion 'dotnet' '10.0.401' { dotnet --version }
        Assert-ToolVersion 'Node.js' 'v26.7.0' { node --version }
        Assert-ToolVersion 'npm' '11.19.0' { npm --version }
        Assert-DocumentationCurrent
        dotnet restore Workbench.slnx --locked-mode | Out-Host
        Assert-NativeCommandSucceeded 'dotnet restore --locked-mode'
        if (-not $SkipDependencyInstall) {
            npm ci --prefix $clientRoot --ignore-scripts --no-audit --no-fund | Out-Host
            Assert-NativeCommandSucceeded 'npm ci'
            npm ci --prefix $browserRoot --ignore-scripts --no-audit --no-fund | Out-Host
            Assert-NativeCommandSucceeded 'browser npm ci'
        }
        $clientStage = Start-VerificationStage -Name client -RepositoryRoot $repositoryRoot -Arguments @($clientRoot) -Action {
            param($client)
            npm run lint --prefix $client
            if ($LASTEXITCODE -ne 0) { throw 'Client lint failed.' }
            npm run test:run --prefix $client -- --maxWorkers=1
            if ($LASTEXITCODE -ne 0) { throw 'Client tests failed.' }
        }
        dotnet format Workbench.slnx --verify-no-changes --no-restore | Out-Host
        Assert-NativeCommandSucceeded 'dotnet format'
        $receipt = New-VerificationBuildReceipt -RepositoryRoot $repositoryRoot -RunId $RunId
        dotnet build Workbench.slnx --configuration Release --no-restore `
            -p:UseAppHost=false -p:BuildClient=false "-p:OpenApiDocumentsDirectory=$openApiRoot" | Out-Host
        Assert-NativeCommandSucceeded 'Release build and OpenAPI document generation'
        $clientStage.Job | Wait-Job | Out-Null
        npm run generate:api --prefix $clientRoot | Out-Host
        Assert-NativeCommandSucceeded 'TypeScript API generation'
        git diff --exit-code -- src/Workbench.Client/openapi/Workbench.Server_beta.json src/Workbench.Client/src/api/generated.ts | Out-Host
        Assert-NativeCommandSucceeded 'generated API drift check'
        & $OnServerReady | Out-Host
        npm run typecheck --prefix $clientRoot | Out-Host
        Assert-NativeCommandSucceeded 'client typecheck'
        npm run build --prefix $clientRoot | Out-Host
        Assert-NativeCommandSucceeded 'client build'
        $manifest = Publish-VerificationArtifacts -RepositoryRoot $repositoryRoot -RunId $RunId -BuildReceipt $receipt -OutputRoot $RunRoot
        $joined = $true
        Complete-VerificationStages -Stages @($clientStage) -RequiredNames @('client') -TimingPath (Join-Path $RunRoot 'client.json')
        [pscustomobject]@{ RunId=$RunId; LocalManifestPath=$manifest; BuildReceipt=$receipt; ClientReceipt=(Get-Content (Join-Path $RunRoot 'client.json') -Raw | ConvertFrom-Json) }
    }
    finally {
        try {
            if ($clientStage -and -not $joined) { Complete-VerificationStages -Stages @($clientStage) -RequiredNames @('client') -TimingPath (Join-Path $RunRoot 'client.json') }
        }
        finally {
            if ($clientStage) { Remove-Job -Job $clientStage.Job -Force -ErrorAction SilentlyContinue }
            Pop-Location
        }
    }
}

function Invoke-VerificationRelease {
    param([string]$RepositoryRoot, [string]$RunId, [string]$ManifestPath, [string]$RunRoot)
    $null = Get-VerificationArtifacts $RepositoryRoot $ManifestPath $RunId
    New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null
    $priorManifest = $env:WORKBENCH_VERIFICATION_MANIFEST
    $priorRun = $env:WORKBENCH_VERIFICATION_RUN
    $stages = @()
    $joined = $false
    try {
        $env:WORKBENCH_VERIFICATION_MANIFEST=$ManifestPath
        $env:WORKBENCH_VERIFICATION_RUN=$RunId
        $stages += Start-VerificationStage -Name browser -RepositoryRoot $RepositoryRoot -Arguments @((Join-Path $RepositoryRoot 'tests/Workbench.BrowserTests')) -Action {
            param($browser)
            npm test --prefix $browser
            if ($LASTEXITCODE -ne 0) { throw 'Browser tests failed.' }
        }
        $stages += Start-VerificationStage -Name published -RepositoryRoot $RepositoryRoot -Action {
            & ./scripts/test-publish.ps1
            if (-not $?) { throw 'Published release-unit verification failed.' }
        }
        $joined = $true
        Complete-VerificationStages -Stages $stages -RequiredNames @('browser','published') -TimingPath (Join-Path $RunRoot 'release.json')
        return @(Get-Content (Join-Path $RunRoot 'release.json') -Raw | ConvertFrom-Json)
    }
    finally {
        try {
            if (-not $joined -and $stages.Count) { Complete-VerificationStages -Stages $stages -RequiredNames @($stages.Name) -TimingPath (Join-Path $RunRoot 'release.json') }
        }
        finally {
            foreach ($stage in $stages) { Remove-Job -Job $stage.Job -Force -ErrorAction SilentlyContinue }
            $env:WORKBENCH_VERIFICATION_MANIFEST=$priorManifest
            $env:WORKBENCH_VERIFICATION_RUN=$priorRun
        }
    }
}

