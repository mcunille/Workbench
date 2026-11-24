$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot)
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = Join-Path $temp "verify-groups-$([guid]::NewGuid().ToString('N'))"
function Invoke-FixtureGit {
    git -C $root @args
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git command failed.' }
}
function global:node { 'v26.8.1'; $global:LASTEXITCODE = 0 }
function global:npm {
    if ($args[0] -eq '--version') { '11.19.0' }
    $global:LASTEXITCODE = 0
}
function global:dotnet {
    if ($args[0] -eq '--version') { '10.0.402' }
    if ($args[0] -eq 'publish') {
        $output = $args[[Array]::IndexOf($args, '--output') + 1]
        New-Item -ItemType Directory -Path $output | Out-Null
        Set-Content -LiteralPath (Join-Path $output 'fixture.txt') 'fresh publish output'
    }
    $global:LASTEXITCODE = 0
}
function global:Start-Job {
    param([scriptblock]$ScriptBlock, [object[]]$ArgumentList)
    $name = $ArgumentList[1]
    $global:verifyFixtureStages.Add($name)
    [pscustomobject]@{ State = 'Completed'; Name = $name }
}
function global:Wait-Job { param([Parameter(ValueFromPipeline)]$Job, [switch]$Any) process { $Job } }
function global:Remove-Job { param($Job, [switch]$Force) }
function global:Receive-Job {
    param($Job, [switch]$Keep)
    if ($Job.Name -eq 'published' -and $global:verifyFixtureMode -eq 'source-change') {
        Set-Content -LiteralPath (Join-Path $global:verifyFixtureRoot 'source.txt') 'changed'
    }
    [pscustomobject]@{ VerificationStage = $Job.Name; Succeeded = ($Job.Name -ne $global:verifyFixtureMode); Seconds = 0; Failure = 'fixture failure' }
}
try {
    $global:verifyFixtureRoot = $root
    $paths = @('scripts/verify.ps1', 'scripts/verification-stages.ps1', 'scripts/verification-artifacts.ps1',
        'docs/operations/database-migrations.md', 'docs/operations/database-backup-restore.md', 'README.md', 'docs/ARCHITECTURE.md')
    foreach ($path in $paths) {
        $destination = Join-Path $root $path
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repository $path) -Destination $destination
    }
    Set-Content -LiteralPath (Join-Path $root '.gitignore') 'artifacts/'
    Set-Content -LiteralPath (Join-Path $root 'source.txt') 'original'
    Invoke-FixtureGit init --quiet
    Invoke-FixtureGit add .
    Invoke-FixtureGit -c user.name=Fixture -c user.email=fixture@example.test commit --quiet -m Fixture
    $revision = Invoke-FixtureGit rev-parse HEAD
    $seen = [Collections.Generic.HashSet[string]]::new()
    foreach ($case in @(
        @{ Group = 'All'; Mode = ''; Stages = 'client,server,browser,published'; Reject = '' },
        @{ Group = 'SourceRelease'; Mode = ''; Stages = 'client,browser,published'; Reject = '' },
        @{ Group = 'SourceRelease'; Mode = 'browser'; Stages = 'client,browser,published'; Reject = 'Required verification stages failed: browser' },
        @{ Group = 'SourceRelease'; Mode = 'source-change'; Stages = 'client,browser,published'; Reject = 'Source changed during verification' }
    )) {
        # GIVEN a verification mode WHEN the real entry point runs THEN launch exactly its stages and record the outcome.
        $global:verifyFixtureMode = $case.Mode
        $global:verifyFixtureStages = [Collections.Generic.List[string]]::new()
        $arguments = @{ SkipDependencyInstall = $true }
        if ($case.Group -ne 'All') { $arguments.StageGroup = $case.Group }
        $failure = ''
        try { & (Join-Path $root 'scripts/verify.ps1') @arguments }
        catch { $failure = $_.Exception.Message }
        if (($case.Reject -eq '' -and $failure -ne '') -or ($case.Reject -ne '' -and -not $failure.Contains($case.Reject))) {
            throw "Unexpected $($case.Group) outcome: $failure"
        }
        $receipts = @(Get-ChildItem (Join-Path $root 'artifacts/verification') -Recurse -Filter gate.json |
            Where-Object { $seen.Add($_.FullName) })
        if ($receipts.Count -ne 1) { throw 'Each invocation must write a fresh receipt.' }
        $receipt = Get-Content -LiteralPath $receipts[0].FullName -Raw | ConvertFrom-Json
        if ($receipt.StageGroup -cne $case.Group -or ($receipt.RequiredStages -join ',') -cne $case.Stages -or
            ($global:verifyFixtureStages -join ',') -cne $case.Stages -or $receipt.Succeeded -ne ($case.Reject -eq '') -or
            $receipt.CheckoutRevision -cne $revision -or $receipt.SourceHash -cnotmatch '^[A-F0-9]{64}$') {
            throw 'Verification receipt or executed stages differ from the requested contract.'
        }
    }
    Write-Host 'Verification stage groups passed through the entry point.'
}
finally {
    foreach ($name in 'node','npm','dotnet','Start-Job','Wait-Job','Receive-Job','Remove-Job') {
        Remove-Item "Function:\$name" -ErrorAction SilentlyContinue
    }
    Get-Variable 'verifyFixture*' -Scope Global | Remove-Variable -Scope Global
    if (Test-Path -LiteralPath $root) {
        $resolved = (Resolve-Path -LiteralPath $root).Path
        if ([IO.Path]::GetDirectoryName($resolved) -ne $temp.TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected fixture location.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
