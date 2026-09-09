# Current-run artifacts are owned by the gate. Consumers validate them but never delete them.
function Get-VerificationSourceHash {
    param([string]$RepositoryRoot, [switch]$BeforeGeneration)
    $paths = @(git -C $RepositoryRoot -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inventory current source for artifact provenance.' }
    $entries = foreach ($relative in ($paths | Sort-Object -Unique)) {
        if ($BeforeGeneration -and $relative -in @('src/Workbench.Client/openapi/Workbench.Server.json', 'src/Workbench.Client/src/api/generated.ts')) { continue }
        $path = Join-Path $RepositoryRoot $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { "$relative=$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)" }
        else { "$relative=MISSING" }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Get-VerificationOutputHash {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Artifact directory missing: $Path" }
    $entries = @(Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName | ForEach-Object {
        "$([IO.Path]::GetRelativePath($Path, $_.FullName))=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    })
    if ($entries.Count -eq 0) { throw 'Artifact directory is empty.' }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))))
}

function New-VerificationBuildReceipt {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$RunId)
    [pscustomobject]@{
        RunId = $RunId
        RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
        SourceHash = Get-VerificationSourceHash $RepositoryRoot -BeforeGeneration
        Configuration = 'Release;UseAppHost=false;BuildClient=false'
    }
}

function Publish-VerificationArtifacts {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)]$BuildReceipt, [Parameter(Mandatory)][string]$OutputRoot)
    $configuration = 'Release;UseAppHost=false;BuildClient=false'
    if ($BuildReceipt.RunId -cne $RunId -or $BuildReceipt.RepositoryRoot -cne [IO.Path]::GetFullPath($RepositoryRoot) -or
        $BuildReceipt.Configuration -cne $configuration -or $BuildReceipt.SourceHash -cne (Get-VerificationSourceHash $RepositoryRoot -BeforeGeneration)) {
        throw 'Source or build configuration changed since this run began building.'
    }
    $serverRoot = Join-Path $OutputRoot 'server'
    $databaseRoot = Join-Path $OutputRoot 'database'
    $manifestPath = Join-Path $OutputRoot 'verification-manifest.json'
    if ((Test-Path $serverRoot) -or (Test-Path $databaseRoot) -or (Test-Path $manifestPath)) { throw 'Artifact output must be fresh for this invocation.' }
    foreach ($project in @(@{ Name = 'Server'; Output = $serverRoot }, @{ Name = 'Database'; Output = $databaseRoot })) {
        & dotnet publish (Join-Path $RepositoryRoot "src/Workbench.$($project.Name)/Workbench.$($project.Name).csproj") `
            --configuration Release --no-build --no-restore --output $project.Output -p:UseAppHost=false -p:BuildClient=false | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Current-run $($project.Name) publish failed with exit code $LASTEXITCODE." }
    }
    if ($BuildReceipt.SourceHash -cne (Get-VerificationSourceHash $RepositoryRoot -BeforeGeneration)) { throw 'Source changed during publish.' }
    $manifest = [ordered]@{
        Version = 1; RunId = $RunId; RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
        Configuration = $configuration; SourceHash = Get-VerificationSourceHash $RepositoryRoot
        ServerRoot = [IO.Path]::GetFullPath($serverRoot); DatabaseRoot = [IO.Path]::GetFullPath($databaseRoot)
        ServerHash = Get-VerificationOutputHash $serverRoot; DatabaseHash = Get-VerificationOutputHash $databaseRoot
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath
    $manifestPath
}

function Get-VerificationArtifacts {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$RunId)
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw 'Current-run verification manifest is missing.' }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.Version -ne 1 -or $manifest.RunId -cne $RunId -or
        $manifest.RepositoryRoot -cne [IO.Path]::GetFullPath($RepositoryRoot) -or
        $manifest.Configuration -cne 'Release;UseAppHost=false;BuildClient=false' -or
        $manifest.SourceHash -cne (Get-VerificationSourceHash $RepositoryRoot)) {
        throw 'Verification manifest does not match current run, source, or configuration.'
    }
    if ($manifest.ServerHash -cne (Get-VerificationOutputHash $manifest.ServerRoot) -or
        $manifest.DatabaseHash -cne (Get-VerificationOutputHash $manifest.DatabaseRoot)) { throw 'Verification artifact content changed.' }
    foreach ($required in @((Join-Path $manifest.ServerRoot 'Workbench.Server.dll'), (Join-Path $manifest.DatabaseRoot 'Workbench.Database.dll'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required verification artifact missing: $required" }
    }
    $manifest
}
