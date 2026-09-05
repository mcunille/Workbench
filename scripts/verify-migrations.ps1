[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Clean', 'Upgrade', 'ReversibleRollback', 'RestoreRollback')]
    [string]$Scenario
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot 'artifacts/migrations'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$logPath = Join-Path $artifactRoot ("{0}-{1}.log" -f $Scenario, (Get-Date -Format 'yyyyMMdd-HHmmss'))
$filters = @{
    Clean = 'FullyQualifiedName~DatabaseMigrationTests.MigratorCreatesCurrentSchemaOnEmptyDatabase'
    Upgrade = 'FullyQualifiedName~DatabaseMigrationTests.MigratorUpgradesASeededPriorSchemaWithoutLosingTenantData'
    ReversibleRollback = 'FullyQualifiedName~DatabaseMigrationTests.LatestMigrationCanRollbackOneVersionAndReapply'
    RestoreRollback = 'FullyQualifiedName~RestoreSanitizationTests.RestoreSanitizationInvalidatesAllAuthenticationArtifacts'
}

Push-Location $repositoryRoot
try {
    dotnet test tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj `
        --no-restore `
        --filter $filters[$Scenario] `
        --logger 'console;verbosity=minimal' `
        -p:OpenApiGenerateDocumentsOnBuild=false 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) {
        throw "Migration scenario '$Scenario' failed. See $logPath."
    }
    Write-Host "Migration scenario '$Scenario' passed; log retained under artifacts/migrations."
}
finally {
    Pop-Location
}
