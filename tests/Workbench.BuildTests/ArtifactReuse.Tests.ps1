[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/verification-artifacts.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('workbench-artifact-tests-' + [Guid]::NewGuid().ToString('N'))
function Assert-Rejected([scriptblock]$Action, [string]$Description) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Expected rejection: $Description" }
}
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    git -C $fixture init --quiet
    Set-Content (Join-Path $fixture 'global.json') '{"sdk":{"version":"10.0.400"}}'
    Set-Content (Join-Path $fixture 'source.cs') 'current source'
    Set-Content (Join-Path $fixture '.gitignore') 'output/'
    # GIVEN a receipt made before the current run builds its source
    $receipt = New-VerificationBuildReceipt -RepositoryRoot $fixture -RunId 'test-run'
    function dotnet {
        $output = $args[[Array]::IndexOf($args, '--output') + 1]
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        $name = if (($args -join ' ') -match 'Workbench.Database.csproj') { 'Workbench.Database.dll' } else { 'Workbench.Server.dll' }
        Set-Content (Join-Path $output $name) 'fresh assembly'
        $global:LASTEXITCODE = 0
    }
    # WHEN freshly built artifacts are published and consumed by the same run
    $manifest = Publish-VerificationArtifacts -RepositoryRoot $fixture -RunId 'test-run' -BuildReceipt $receipt -OutputRoot (Join-Path $fixture 'output')
    $artifacts = Get-VerificationArtifacts -RepositoryRoot $fixture -ManifestPath $manifest -RunId 'test-run'
    # THEN current artifacts are accepted and a replay or missing manifest is rejected
    if (-not $artifacts.ServerRoot) { throw 'Missing artifact roots.' }
    Assert-Rejected { Get-VerificationArtifacts -RepositoryRoot $fixture -ManifestPath $manifest -RunId 'other-run' } 'replayed run'
    Assert-Rejected { Get-VerificationArtifacts -RepositoryRoot $fixture -ManifestPath "$manifest-missing" -RunId 'test-run' } 'missing manifest'
    # WHEN manifest configuration is altered THEN it cannot authorize incompatible output
    $originalManifest = Get-Content $manifest -Raw
    $incompatible = $originalManifest | ConvertFrom-Json
    $incompatible.Configuration = 'Debug'
    $incompatible | ConvertTo-Json | Set-Content $manifest
    Assert-Rejected { Get-VerificationArtifacts -RepositoryRoot $fixture -ManifestPath $manifest -RunId 'test-run' } 'incompatible configuration'
    Set-Content $manifest $originalManifest
    # WHEN an untracked source input changes THEN provenance fails
    Set-Content (Join-Path $fixture 'source.cs') 'changed source'
    Assert-Rejected { Get-VerificationArtifacts -RepositoryRoot $fixture -ManifestPath $manifest -RunId 'test-run' } 'changed untracked source'
    Assert-Rejected { Publish-VerificationArtifacts -RepositoryRoot $fixture -RunId 'test-run' -BuildReceipt $receipt -OutputRoot (Join-Path $fixture 'output/new') } 'stale build receipt'
    Set-Content (Join-Path $fixture 'source.cs') 'current source'
    # WHEN configuration changes THEN provenance fails
    Set-Content (Join-Path $fixture 'global.json') 'changed SDK'
    Assert-Rejected { Get-VerificationArtifacts -RepositoryRoot $fixture -ManifestPath $manifest -RunId 'test-run' } 'changed configuration'
    Set-Content (Join-Path $fixture 'global.json') '{"sdk":{"version":"10.0.400"}}'
    # WHEN output changes THEN artifact integrity fails
    Set-Content (Join-Path $artifacts.ServerRoot 'Workbench.Server.dll') 'stale assembly'
    Assert-Rejected { Get-VerificationArtifacts -RepositoryRoot $fixture -ManifestPath $manifest -RunId 'test-run' } 'changed artifact'
    # WHEN a required assembly disappears THEN reuse fails even when the manifest hashes are rewritten
    Remove-Item -LiteralPath (Join-Path $artifacts.DatabaseRoot 'Workbench.Database.dll')
    Set-Content (Join-Path $artifacts.DatabaseRoot 'unrelated.txt') 'not the tool'
    $missing = $originalManifest | ConvertFrom-Json
    $missing.ServerHash = Get-VerificationOutputHash $artifacts.ServerRoot
    $missing.DatabaseHash = Get-VerificationOutputHash $artifacts.DatabaseRoot
    $missing | ConvertTo-Json | Set-Content $manifest
    Assert-Rejected { Get-VerificationArtifacts -RepositoryRoot $fixture -ManifestPath $manifest -RunId 'test-run' } 'required database DLL missing'
    Write-Host 'Artifact reuse tests passed.'
}
finally {
    if ([IO.Path]::GetFullPath($fixture).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) -and [IO.Path]::GetFileName($fixture).StartsWith('workbench-artifact-tests-')) {
        Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}
