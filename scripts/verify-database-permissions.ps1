[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
dotnet test (Join-Path $repositoryRoot 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj') `
    --filter 'FullyQualifiedName~BootstrapTests|FullyQualifiedName~DatabasePermissionTests'
if ($LASTEXITCODE -ne 0) {
    throw 'Workbench database permission verification failed.'
}
