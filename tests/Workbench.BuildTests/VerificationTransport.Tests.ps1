$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/verification-artifacts.ps1')
. (Join-Path $PSScriptRoot '../../scripts/server-partition-contract.ps1')
$transportScript = Join-Path $PSScriptRoot '../../scripts/verification-transport.ps1'
if (Test-Path $transportScript) { . $transportScript }
function Assert-Rejected([scriptblock]$Action, [string]$Category) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -match $Category) { return }; throw }
    throw "Expected rejection: $Category"
}
if (-not (Get-Command Export-VerificationTransport -ErrorAction SilentlyContinue)) { throw 'Portable artifact behavior is missing.' }
$root = Join-Path ([IO.Path]::GetTempPath()) "verification-transport-$([guid]::NewGuid().ToString('N'))"
try {
    $producer = Join-Path $root 'producer'; $consumer = Join-Path $root 'consumer'
    New-Item -ItemType Directory -Path $producer | Out-Null
    git -C $producer init --quiet
    Set-Content (Join-Path $producer 'source.cs') 'current source'
    Set-Content (Join-Path $producer '.gitignore') 'output/'
    git -C $producer add .
    git -C $producer -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m fixture
    if ($LASTEXITCODE) { throw 'Fixture Git preparation failed.' }
    git clone --quiet --no-hardlinks $producer $consumer
    $revision = (git -C $producer rev-parse HEAD).Trim()
    $context = [pscustomobject]@{ Repository='mcunille/Workbench'; Revision=$revision; Workflow='CI'; RunId='123'; RunAttempt='1'; InvocationId='fixture'; Configuration='Release;UseAppHost=false;BuildClient=false'; Toolchain=[pscustomobject]@{ Sdk='10.0.401'; Node='26.7.0'; Npm='11.19.0' }; Platform=[pscustomobject]@{ OS=[Runtime.InteropServices.RuntimeInformation]::OSDescription; Architecture='X64' } }
    $receipt = New-VerificationBuildReceipt $producer $context.InvocationId
    function dotnet {
        $output = $args[[Array]::IndexOf($args, '--output')+1]
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        $name = if (($args -join ' ') -match 'Workbench.Database.csproj') { 'Workbench.Database.dll' } else { 'Workbench.Server.dll' }
        Set-Content (Join-Path $output $name) 'fresh runtime'
        $global:LASTEXITCODE=0
    }
    $local = Publish-VerificationArtifacts $producer $context.InvocationId $receipt (Join-Path $producer 'output/published')
    $runtime = @{}
    foreach ($name in @('client','tests')) { $runtime[$name] = Join-Path $producer "output/$name"; New-Item -ItemType Directory -Path $runtime[$name] | Out-Null }
    Set-Content (Join-Path $runtime.client 'index.html') 'client'
    Set-Content (Join-Path $runtime.tests 'Workbench.Server.IntegrationTests.dll') 'tests'
    $runtime.server = Join-Path $producer 'output/published/server'; $runtime.database = Join-Path $producer 'output/published/database'
    $testNames=@('Suite.A','Suite.B','Suite.C','Suite.D')
    $plan = New-DistributedServerPlan $testNames @(New-ServerTestPartitions $testNames 4) @{Sha256=('a'*64)} @('tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj')
    # GIVEN current-run source/build evidence WHEN exported and relocated THEN local path checks remain strict.
    $bundle = Export-VerificationTransport $producer $context $receipt $local $plan $runtime (Join-Path $producer 'output/bundle')
    $imported = Import-VerificationTransport $consumer $bundle.BundleRoot $context $bundle.TransportHash $bundle.PlanHash (Join-Path $consumer 'output/valid')
    $validated = Get-VerificationArtifacts $consumer $imported.LocalManifestPath $context.InvocationId
    if (-not $validated.ServerRoot.StartsWith($consumer, [StringComparison]::Ordinal)) { throw 'Artifacts were not relocated.' }
    Assert-Rejected { Get-VerificationArtifacts $consumer $local $context.InvocationId } 'manifest does not match'
    # GIVEN current source changed, including an untracked input WHEN imported THEN stale reuse is rejected.
    Set-Content (Join-Path $consumer 'new.cs') 'unexpected source'
    Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context $bundle.TransportHash $bundle.PlanHash (Join-Path $consumer 'output/stale') } 'source'
    Remove-Item -LiteralPath (Join-Path $consumer 'new.cs')
    # GIVEN a replayed/altered trusted context WHEN consumed THEN no local authorization manifest exists.
    foreach ($field in @('Repository','Revision','Workflow','RunId','RunAttempt','InvocationId','Configuration','Toolchain','Platform')) {
        $wrong = $context.PSObject.Copy(); $wrong.$field = 'wrong'
        $destination = Join-Path $consumer "output/wrong-$field"
        Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $wrong $bundle.TransportHash $bundle.PlanHash $destination } 'context|configuration|toolchain|platform'
        if (Test-Path (Join-Path $destination 'verification-manifest.json')) { throw 'Rejected import authorized output.' }
    }
    # GIVEN payload tampering, absence or undeclared output WHEN validated THEN hashes and exact sets protect reuse.
    $payload = Join-Path $bundle.BundleRoot 'payload/tests/Workbench.Server.IntegrationTests.dll'
    $original = [IO.File]::ReadAllBytes($payload)
    Set-Content $payload 'changed'
    Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context $bundle.TransportHash $bundle.PlanHash (Join-Path $consumer 'output/tampered') } 'content'
    Remove-Item -LiteralPath $payload
    Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context $bundle.TransportHash $bundle.PlanHash (Join-Path $consumer 'output/missing') } 'file set|missing'
    [IO.File]::WriteAllBytes($payload, $original)
    Set-Content (Join-Path $bundle.BundleRoot 'payload/tests/extra.dll') 'undeclared'
    Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context $bundle.TransportHash $bundle.PlanHash (Join-Path $consumer 'output/extra') } 'file set'
    Remove-Item -LiteralPath (Join-Path $bundle.BundleRoot 'payload/tests/extra.dll')
    # GIVEN malformed producer manifests WHEN even their new digest is supplied THEN unsafe metadata fails closed.
    $manifestText = Get-Content $bundle.ManifestPath -Raw
    foreach ($unsafe in @('../escape','/absolute','C:/absolute','payload/../escape','payload\escape')) {
        $bad = $manifestText | ConvertFrom-Json; $bad.Files[0].Path=$unsafe
        $bad | ConvertTo-Json -Depth 20 | Set-Content $bundle.ManifestPath
        $digest = (Get-FileHash $bundle.ManifestPath).Hash
        Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context $digest $bundle.PlanHash (Join-Path $consumer ('output/unsafe-'+[guid]::NewGuid().ToString('N'))) } 'path'
    }
    $bad = $manifestText | ConvertFrom-Json; $bad.Files += $bad.Files[0]
    $bad | ConvertTo-Json -Depth 20 | Set-Content $bundle.ManifestPath
    Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context (Get-FileHash $bundle.ManifestPath).Hash $bundle.PlanHash (Join-Path $consumer 'output/duplicate') } 'duplicate'
    $bad = $manifestText | ConvertFrom-Json; $bad.Version=99
    $bad | ConvertTo-Json -Depth 20 | Set-Content $bundle.ManifestPath
    Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context (Get-FileHash $bundle.ManifestPath).Hash $bundle.PlanHash (Join-Path $consumer 'output/version') } 'version'
    Set-Content $bundle.ManifestPath $manifestText -NoNewline
    Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context ('0'*64) $bundle.PlanHash (Join-Path $consumer 'output/digest') } 'digest'
    # GIVEN an unknown test assembly in a producer plan THEN reject before authorizing execution.
    $planPath=Join-Path $bundle.BundleRoot 'plan.json'
    $planText=Get-Content $planPath -Raw
    $badPlan=$planText|ConvertFrom-Json; $badPlan.TestProjects=@('unknown.csproj')
    $badPlan|ConvertTo-Json -Depth 30|Set-Content $planPath
    $badManifest=$manifestText|ConvertFrom-Json; $badManifest.PlanHash=(Get-FileHash $planPath).Hash
    $badManifest|ConvertTo-Json -Depth 30|Set-Content $bundle.ManifestPath
    Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context (Get-FileHash $bundle.ManifestPath).Hash $badManifest.PlanHash (Join-Path $consumer 'output/assembly') } 'project inventory'
    Set-Content $planPath $planText -NoNewline
    Set-Content $bundle.ManifestPath $manifestText -NoNewline
    # GIVEN a reparse-point directory WHEN inventoried THEN content outside the bundle is never traversed.
    $link = Join-Path $bundle.BundleRoot 'payload/tests/link'
    New-Item -ItemType $(if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }) -Path $link -Target $consumer | Out-Null
    Assert-Rejected { Import-VerificationTransport $consumer $bundle.BundleRoot $context $bundle.TransportHash $bundle.PlanHash (Join-Path $consumer 'output/link') } 'link'
    Remove-Item -LiteralPath $link -Force
    Write-Host 'Portable artifact validation passed.'
}
finally {
    Remove-Item Function:dotnet -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('verification-transport-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
