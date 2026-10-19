[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Server','Release','Aggregate')][string]$Phase,
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$ExpectedContextPath,
    [string]$BundleRoot,
    [string]$ExpectedTransportHash,
    [string]$ExpectedPlanHash,
    [ValidateSet('A','B')][string]$HostId,
    [string]$EvidenceRoot,
    [string]$JobResultsPath,
    [switch]$SkipDependencyInstall
)
$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'verification-phases.ps1')
. (Join-Path $PSScriptRoot 'verification-aggregate.ps1')
. (Join-Path $PSScriptRoot 'server-partition-execution.ps1')
$context=Get-Content -LiteralPath $ExpectedContextPath -Raw | ConvertFrom-Json
Assert-VerificationTransportContext $context $context
if (Test-Path -LiteralPath $RunRoot) { throw 'Distributed phase run directory must be fresh.' }
New-Item -ItemType Directory -Path $RunRoot | Out-Null
$clock=[Diagnostics.Stopwatch]::StartNew()
$succeeded=$false
$stages=@(); $partitionResults=@(); $imported=$null; $exported=$null
try {
    if ($Phase -ceq 'Prepare') {
        $prepared=Invoke-VerificationPreparation -RepositoryRoot $repositoryRoot -RunId $context.InvocationId -RunRoot (Join-Path $RunRoot 'preparation') -SkipDependencyInstall:$SkipDependencyInstall
        $stages=@($prepared.ClientReceipt)
        [xml]$solution=Get-Content -LiteralPath (Join-Path $repositoryRoot 'Workbench.slnx') -Raw
        $projects=@(foreach ($node in $solution.SelectNodes('//Project')) {
            $value=@(dotnet msbuild (Join-Path $repositoryRoot $node.Path) '-p:Configuration=Release' '-getProperty:IsTestProject')
            if ($LASTEXITCODE -ne 0) { throw 'Solution test project discovery failed.' }
            if (($value -join '').Trim() -ieq 'true') { $node.Path.Replace('\','/') }
        })
        Assert-ServerTestProjects $projects 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj'
        $assembly=Join-Path $repositoryRoot 'tests/Workbench.Server.IntegrationTests/bin/Release/net10.0/Workbench.Server.IntegrationTests.dll'
        $discoveryPath=Join-Path $RunRoot 'discovery.log'
        $names=@(Invoke-ServerTestDiscovery -LogPath $discoveryPath -Command { dotnet vstest $assembly --ListTests })
        $timings=Read-ServerTestDurations (Join-Path $PSScriptRoot 'server-test-durations.json')
        $parts=@(New-ServerTestPartitions $names 4 $timings.Durations $timings.FallbackSeconds)
        $dataset=$timings | Select-Object SchemaVersion,SourceRevision,SourceRunUrl,Sha256,FallbackSeconds
        $plan=New-DistributedServerPlan $names $parts $dataset $projects
        $plan | Add-Member -NotePropertyName DiscoveryOutput -NotePropertyValue @(Get-Content -LiteralPath $discoveryPath)
        $local=Get-VerificationArtifacts $repositoryRoot $prepared.LocalManifestPath $context.InvocationId
        $runtime=@{ server=$local.ServerRoot; database=$local.DatabaseRoot; client=(Join-Path $repositoryRoot 'src/Workbench.Client/dist'); tests=(Split-Path $assembly) }
        $exported=Export-VerificationTransport $repositoryRoot $context $prepared.BuildReceipt $prepared.LocalManifestPath $plan $runtime (Join-Path $RunRoot 'bundle')
        $ExpectedTransportHash=$exported.TransportHash; $ExpectedPlanHash=$exported.PlanHash
        if ($env:GITHUB_OUTPUT) {
            @("invocation-id=$($context.InvocationId)","transport-hash=$ExpectedTransportHash","plan-hash=$ExpectedPlanHash") | Add-Content -LiteralPath $env:GITHUB_OUTPUT
        }
    }
    else {
        if (-not $BundleRoot -or -not $ExpectedTransportHash -or -not $ExpectedPlanHash) { throw 'Distributed consumers require trusted producer bundle identity.' }
        $imported=Import-VerificationTransport $repositoryRoot $BundleRoot $context $ExpectedTransportHash $ExpectedPlanHash (Join-Path $RunRoot 'runtime')
        switch ($Phase) {
            Server {
                if (-not $HostId) { throw 'Server phase requires a host assignment.' }
                $hostEntry=@($imported.Plan.Hosts | Where-Object Id -CEQ $HostId)
                $expected=if ($HostId -ceq 'A') {@('1','2')} else {@('3','4')}
                if ($hostEntry.Count -ne 1) { throw 'Missing server host assignment.' }
                Assert-ServerTestCoverage $expected @($hostEntry[0].PartitionIds | ForEach-Object {[string]$_})
                $assigned=@($imported.Plan.Partitions | Where-Object { [string]$_.Id -cin $expected })
                $null=Invoke-ServerPartitionExecution $repositoryRoot $imported.TestAssemblyPath $assigned $RunRoot 2
            }
            Release {
                $stages=@(Invoke-VerificationRelease $repositoryRoot $context.InvocationId $imported.LocalManifestPath $RunRoot)
            }
            Aggregate {
                if (-not $EvidenceRoot -or -not $JobResultsPath) { throw 'Aggregate phase requires current-run evidence and job results.' }
                $receipts=@(foreach ($job in @('prepare','release','server-a','server-b')) {
                    Get-Content -LiteralPath (Join-Path $EvidenceRoot "$job/receipt.json") -Raw | ConvertFrom-Json
                })
                $jobs=Get-Content -LiteralPath $JobResultsPath -Raw | ConvertFrom-Json -AsHashtable
                $summary=Assert-DistributedVerification $imported $receipts $jobs $EvidenceRoot
                $summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $RunRoot 'gate.json')
            }
        }
    }
    $succeeded=$true
}
finally {
    if ($Phase -ceq 'Server') {
        foreach ($id in $(if ($HostId -ceq 'A') {@(1,2)} else {@(3,4)})) {
            $timingPath=Join-Path $RunRoot "partition-$id/timing.json"
            $trx=Join-Path $RunRoot "partition-$id/results.trx"
            if (Test-Path -LiteralPath $timingPath) {
                $timing=Get-Content -LiteralPath $timingPath -Raw | ConvertFrom-Json
                $partitionResults += [pscustomobject]@{ PartitionId=$id; ExitCode=$timing.ExitCode; Seconds=$timing.Seconds;
                    TrxPath="server-$($HostId.ToLowerInvariant())/partition-$id/results.trx";
                    TrxHash=$(if (Test-Path -LiteralPath $trx) {(Get-FileHash -LiteralPath $trx -Algorithm SHA256).Hash} else {''}) }
            }
        }
    }
    if ($Phase -cne 'Aggregate') {
        [ordered]@{ Version=1; Context=$context; TransportHash=$ExpectedTransportHash; PlanHash=$ExpectedPlanHash;
            Phase=$Phase; HostId=$(if ($Phase -ceq 'Server') {$HostId} else {''}); Succeeded=$succeeded;
            Seconds=$clock.Elapsed.TotalSeconds; Partitions=$partitionResults; Stages=$stages
        } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $RunRoot 'receipt.json')
    }
    Write-Host "Distributed phase $Phase success=$succeeded; evidence: $RunRoot. A phase is not a complete verification gate."
}
