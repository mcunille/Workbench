. (Join-Path $PSScriptRoot 'verification-artifacts.ps1')
. (Join-Path $PSScriptRoot 'server-partition-contract.ps1')

function Assert-VerificationTransportPath {
    param([string]$Path)
    if (-not $Path -or $Path -match '[\\:\x00]' -or $Path.StartsWith('/') -or
        @($Path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) { throw "Unsafe transport path: $Path" }
    if ($Path -cnotmatch '^payload/(server|database|client|tests)/.+$') { throw "Unexpected transport path: $Path" }
}

function Get-VerificationTransportFiles {
    param([string]$Root)
    $item = Get-Item -LiteralPath $Root -Force -ErrorAction Stop
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Transport links are forbidden.' }
    foreach ($child in Get-ChildItem -LiteralPath $Root -Force) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Transport links are forbidden.' }
        if ($child.PSIsContainer) { Get-VerificationTransportFiles -Root $child.FullName }
        else { $child.FullName }
    }
}

function Assert-VerificationDestinationAncestors {
    param([string]$Path)
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Transport destination links are forbidden.' }
        }
        $parent=Split-Path $current -Parent
        if ($parent -ceq $current) { break }
        $current=$parent
    }
}

function Assert-VerificationTransportContext {
    param([object]$Actual, [object]$Expected)
    foreach ($key in @('Repository','Revision','Workflow','RunId','RunAttempt','InvocationId','Configuration')) {
        if (-not $Actual.$key -or [string]$Actual.$key -cne [string]$Expected.$key) { throw "Transport context mismatch: $key" }
    }
    foreach ($group in @(@('Toolchain','Sdk','Node','Npm'), @('Platform','OS','Architecture'))) {
        foreach ($key in $group[1..($group.Count-1)]) {
            if (-not $Actual.($group[0]).$key -or [string]$Actual.($group[0]).$key -cne [string]$Expected.($group[0]).$key) { throw "Transport context mismatch: $($group[0]).$key" }
        }
    }
    if ($Actual.Configuration -cne 'Release;UseAppHost=false;BuildClient=false' -or $Actual.Toolchain.Sdk -cne '10.0.401' -or
        $Actual.Toolchain.Node -cne '26.7.0' -or $Actual.Toolchain.Npm -cne '11.19.0') { throw 'Unsupported transport configuration/toolchain.' }
}

function Export-VerificationTransport {
    param([string]$RepositoryRoot, [object]$Context, [object]$BuildReceipt, [string]$LocalManifestPath,
        [object]$Plan, [hashtable]$RuntimeRoots, [string]$OutputRoot)
    Assert-VerificationTransportContext $Context $Context
    $revision = (git -C $RepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -cne $Context.Revision) { throw 'Transport source revision mismatch.' }
    $local = Get-VerificationArtifacts $RepositoryRoot $LocalManifestPath $Context.InvocationId
    if ($BuildReceipt.RunId -cne $Context.InvocationId -or $BuildReceipt.RepositoryRoot -cne [IO.Path]::GetFullPath($RepositoryRoot) -or
        $BuildReceipt.Configuration -cne $Context.Configuration -or $BuildReceipt.SourceHash -cne (Get-VerificationSourceHash $RepositoryRoot -BeforeGeneration)) { throw 'Transport source build receipt mismatch.' }
    Assert-DistributedServerPlan $Plan
    if ($RuntimeRoots.Count -ne 4 -or @('server','database','client','tests' | Where-Object { -not $RuntimeRoots.ContainsKey($_) }).Count -or
        [IO.Path]::GetFullPath($RuntimeRoots.server) -cne $local.ServerRoot -or [IO.Path]::GetFullPath($RuntimeRoots.database) -cne $local.DatabaseRoot) { throw 'Unexpected transport runtime roots.' }
    if (Test-Path -LiteralPath $OutputRoot) { throw 'Transport output must be fresh.' }
    New-Item -ItemType Directory -Path $OutputRoot | Out-Null
    $files = [Collections.Generic.List[object]]::new()
    foreach ($name in @('server','database','client','tests')) {
        $root = [IO.Path]::GetFullPath($RuntimeRoots[$name])
        $inputs = @(Get-VerificationTransportFiles $root)
        if (-not $inputs.Count) { throw "Transport runtime root is empty: $name" }
        foreach ($inputFile in $inputs) {
            $relative = "payload/$name/" + [IO.Path]::GetRelativePath($root, $inputFile).Replace('\','/')
            Assert-VerificationTransportPath $relative
            # These runtime trees must never contain private credentials, database backups or build caches.
            if ($relative -match '(?i)(^|/)(obj|node_modules|\.env[^/]*|secrets)(/|$)|\.(bak|connection|password)$') { throw "Forbidden transport content path: $relative" }
            $target = Join-Path $OutputRoot $relative
            New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item -LiteralPath $inputFile -Destination $target
            $files.Add([pscustomobject]@{ Path=$relative; Hash=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash })
        }
    }
    $planPath = Join-Path $OutputRoot 'plan.json'
    $Plan | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $planPath -Encoding utf8NoBOM
    $planHash = (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash
    $manifest = [ordered]@{ Version=1; Context=$Context; SourceHash=$local.SourceHash; BeforeGenerationSourceHash=$BuildReceipt.SourceHash; PlanHash=$planHash; Files=@($files.ToArray() | Sort-Object -CaseSensitive Path) }
    $manifestPath = Join-Path $OutputRoot 'transport.json'
    $manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    if ($local.SourceHash -cne (Get-VerificationSourceHash $RepositoryRoot)) { throw 'Transport source changed during export.' }
    [pscustomobject]@{ BundleRoot=[IO.Path]::GetFullPath($OutputRoot); ManifestPath=$manifestPath; TransportHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash; PlanHash=$planHash }
}

function Import-VerificationTransport {
    param([string]$RepositoryRoot, [string]$BundleRoot, [object]$ExpectedContext, [string]$ExpectedTransportHash,
        [string]$ExpectedPlanHash, [string]$OutputRoot)
    $allFiles = @(Get-VerificationTransportFiles $BundleRoot)
    $manifestPath = Join-Path $BundleRoot 'transport.json'
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -cne $ExpectedTransportHash) { throw 'Transport manifest digest mismatch.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Version -ne 1) { throw 'Unsupported transport version.' }
    Assert-VerificationTransportContext $manifest.Context $ExpectedContext
    $revision = (git -C $RepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -cne $ExpectedContext.Revision -or
        $manifest.SourceHash -cne (Get-VerificationSourceHash $RepositoryRoot) -or
        $manifest.BeforeGenerationSourceHash -cne (Get-VerificationSourceHash $RepositoryRoot -BeforeGeneration)) { throw 'Transport source mismatch.' }
    $planPath = Join-Path $BundleRoot 'plan.json'
    if ($manifest.PlanHash -cne $ExpectedPlanHash -or (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash -cne $ExpectedPlanHash) { throw 'Transport plan digest mismatch.' }
    $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    Assert-DistributedServerPlan $plan
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $manifest.Files) {
        Assert-VerificationTransportPath $file.Path
        if (-not $seen.Add($file.Path)) { throw 'Duplicate transport path.' }
    }
    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $allFiles) { $null = $actual.Add([IO.Path]::GetRelativePath([IO.Path]::GetFullPath($BundleRoot), $file).Replace('\','/')) }
    $expected = [Collections.Generic.HashSet[string]]::new($seen, [StringComparer]::Ordinal)
    $null = $expected.Add('transport.json'); $null = $expected.Add('plan.json')
    if (-not $expected.SetEquals($actual)) { throw 'Transport file set mismatch.' }
    foreach ($file in $manifest.Files) {
        if ((Get-FileHash -LiteralPath (Join-Path $BundleRoot $file.Path) -Algorithm SHA256).Hash -cne $file.Hash) { throw 'Transport artifact content changed.' }
    }
    foreach ($required in @('payload/server/Workbench.Server.dll','payload/database/Workbench.Database.dll','payload/client/index.html','payload/tests/Workbench.Server.IntegrationTests.dll')) {
        if (-not $seen.Contains($required)) { throw "Required transport runtime missing: $required" }
    }
    if (Test-Path -LiteralPath $OutputRoot) { throw 'Transport import output must be fresh.' }
    # Existing CLI integration cases derive these sibling paths from AppContext.BaseDirectory.
    # Preserve that layout using only verified files; never rebuild or overwrite another invocation.
    $testRoot=Join-Path $RepositoryRoot 'tests/Workbench.Server.IntegrationTests/bin/Release/net10.0'
    $databaseRoot=Join-Path $RepositoryRoot 'src/Workbench.Database/bin/Release/net10.0'
    foreach ($destination in @($OutputRoot,$testRoot,$databaseRoot)) { Assert-VerificationDestinationAncestors $destination }
    if ((Test-Path -LiteralPath $testRoot) -or (Test-Path -LiteralPath $databaseRoot)) { throw 'Transport runtime layout must be fresh.' }
    New-Item -ItemType Directory -Path $OutputRoot | Out-Null
    foreach ($file in $manifest.Files) {
        $target = if ($file.Path.StartsWith('payload/tests/',[StringComparison]::Ordinal)) {
            Join-Path $testRoot $file.Path.Substring('payload/tests/'.Length)
        } elseif ($file.Path.StartsWith('payload/database/',[StringComparison]::Ordinal)) {
            Join-Path $databaseRoot $file.Path.Substring('payload/database/'.Length)
        } else { Join-Path $OutputRoot $file.Path }
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $BundleRoot $file.Path) -Destination $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $file.Hash) { throw 'Transport content changed during import.' }
    }
    $server = [IO.Path]::GetFullPath((Join-Path $OutputRoot 'payload/server'))
    $database = [IO.Path]::GetFullPath($databaseRoot)
    $localPath = Join-Path $OutputRoot 'verification-manifest.json'
    [ordered]@{ Version=1; RunId=$ExpectedContext.InvocationId; RepositoryRoot=[IO.Path]::GetFullPath($RepositoryRoot); Configuration=$ExpectedContext.Configuration;
        SourceHash=$manifest.SourceHash; ServerRoot=$server; DatabaseRoot=$database;
        ServerHash=(Get-VerificationOutputHash $server); DatabaseHash=(Get-VerificationOutputHash $database)
    } | ConvertTo-Json | Set-Content -LiteralPath $localPath -Encoding utf8NoBOM
    $null = Get-VerificationArtifacts $RepositoryRoot $localPath $ExpectedContext.InvocationId
    [pscustomobject]@{ Context=$manifest.Context; Plan=$plan; TransportHash=$ExpectedTransportHash; PlanHash=$ExpectedPlanHash;
        TestAssemblyPath=(Join-Path $testRoot 'Workbench.Server.IntegrationTests.dll'); LocalManifestPath=$localPath }
}
