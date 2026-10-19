$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../scripts/server-partition-contract.ps1')
$aggregateScript=Join-Path $PSScriptRoot '../../scripts/verification-aggregate.ps1'
if (Test-Path $aggregateScript) { . $aggregateScript }
function Assert-Rejected([scriptblock]$Action,[string]$Category) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -match $Category) { return }; throw }
    throw "Expected rejection: $Category"
}
if (-not (Get-Command Assert-DistributedVerification -ErrorAction SilentlyContinue)) { throw 'Strict distributed aggregation behavior is missing.' }
$root=Join-Path ([IO.Path]::GetTempPath()) "distributed-aggregate-$([guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $names=@('Suite.T(v: "A")','Suite.T(v: "a")','Suite.U(v: "漢字")','Suite.C','Suite.D')
    $plan=New-DistributedServerPlan $names @(New-ServerTestPartitions $names 4) @{Sha256=('a'*64)} @('tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj')
    $context=[pscustomobject]@{ Repository='mcunille/Workbench'; Revision=('a'*40); Workflow='CI'; RunId='12'; RunAttempt='1'; InvocationId='fixture'; Configuration='Release;UseAppHost=false;BuildClient=false'; Toolchain=@{Sdk='10.0.401';Node='26.7.0';Npm='11.19.0'};Platform=@{OS='Linux';Architecture='X64'} }
    $imported=[pscustomobject]@{ Context=$context; Plan=$plan; TransportHash=('b'*64); PlanHash=('c'*64) }
    $jobs=@{}; $receipts=@()
    foreach ($jobId in @('prepare','release','server-a','server-b')) {
        $jobs[$jobId]=@{result='success'}
        $phase=if ($jobId -eq 'prepare') {'Prepare'} elseif ($jobId -eq 'release') {'Release'} else {'Server'}
        $hostId=if ($phase -eq 'Server') {$jobId.Substring(7).ToUpperInvariant()} else {''}
        $stages=if ($phase -eq 'Prepare') {@([pscustomobject]@{Stage='client';Succeeded=$true})} elseif ($phase -eq 'Release') {@([pscustomobject]@{Stage='browser';Succeeded=$true},[pscustomobject]@{Stage='published';Succeeded=$true})} else {@()}
        $partReceipts=@()
        foreach ($part in @($plan.Partitions | Where-Object { $_.Id -in @(($plan.Hosts | Where-Object Id -CEQ $hostId).PartitionIds) })) {
            $relative="$jobId/partition-$($part.Id)/results.trx"
            $trx=Join-Path $root $relative
            New-Item -ItemType Directory -Path (Split-Path $trx) -Force | Out-Null
            $rows=($part.Tests | ForEach-Object {'<UnitTestResult testName="'+[Security.SecurityElement]::Escape($_)+'" outcome="Passed" />'}) -join ''
            Set-Content $trx "<TestRun><Results>$rows</Results></TestRun>"
            $partReceipts += [pscustomobject]@{PartitionId=$part.Id;ExitCode=0;Seconds=1;TrxPath=$relative;TrxHash=(Get-FileHash $trx).Hash}
        }
        $receipts += [pscustomobject]@{Version=1;Context=$context;TransportHash=$imported.TransportHash;PlanHash=$imported.PlanHash;Phase=$phase;HostId=$hostId;Succeeded=$true;Seconds=1;Partitions=$partReceipts;Stages=$stages}
    }
    # GIVEN complete current-run receipts and case-distinct TRX WHEN aggregated THEN every case passes exactly once.
    $result=Assert-DistributedVerification $imported $receipts $jobs $root
    if (-not $result.Succeeded -or $result.Cases -ne $names.Count) {throw 'Valid exact inventory rejected.'}
    $json=$receipts | ConvertTo-Json -Depth 25
    # GIVEN a job cancelled after successful upload, or missing/failed/skipped dependencies THEN never infer success from receipts.
    foreach ($job in @('prepare','release','server-a','server-b')) {
        foreach ($state in @('cancelled','failure','skipped','')) { $jobs[$job].result=$state; Assert-Rejected {Assert-DistributedVerification $imported $receipts $jobs $root} $job }
        $jobs.Remove($job); Assert-Rejected {Assert-DistributedVerification $imported $receipts $jobs $root} $job
        $jobs[$job]=@{result='success'}
    }
    # GIVEN stale or altered phase identity WHEN checked THEN context and all mandatory phases are enforced.
    foreach ($field in @('TransportHash','PlanHash','Version','Succeeded')) {
        $bad=$json|ConvertFrom-Json; $bad[2].$field=if ($field -eq 'Succeeded') {$false} else {'wrong'}
        Assert-Rejected {Assert-DistributedVerification $imported $bad $jobs $root} 'receipt|context|version'
    }
    $bad=$json|ConvertFrom-Json; $bad[2].Context.RunAttempt='2'
    Assert-Rejected {Assert-DistributedVerification $imported $bad $jobs $root} 'context'
    Assert-Rejected {Assert-DistributedVerification $imported $receipts[0..2] $jobs $root} 'receipt'
    Assert-Rejected {Assert-DistributedVerification $imported @($receipts[0],$receipts[1],$receipts[2],$receipts[2]) $jobs $root} 'receipt'
    # GIVEN duplicate, missing or wrong assigned IDs THEN global cardinality alone is insufficient.
    foreach ($mode in @('duplicate','missing','exit','path','hash')) {
        $bad=$json|ConvertFrom-Json
        switch ($mode) {
            duplicate {$bad[2].Partitions[1]=$bad[2].Partitions[0]}
            missing {$bad[2].Partitions=@($bad[2].Partitions[0])}
            exit {$bad[2].Partitions[0].ExitCode=7}
            path {$bad[2].Partitions[0].TrxPath='../escape'}
            hash {$bad[2].Partitions[0].TrxHash=('0'*64)}
        }
        Assert-Rejected {Assert-DistributedVerification $imported $bad $jobs $root} 'partition|coverage|digest|path'
    }
    # GIVEN tampered outcomes or substituted/duplicate cases with rehashed receipts THEN TRX semantics still fail.
    $path=Join-Path $root $receipts[2].Partitions[0].TrxPath
    $original=Get-Content $path -Raw
    foreach ($outcome in @('Failed','NotExecuted','Aborted')) {
        Set-Content $path ($original.Replace('Passed',$outcome))
        $bad=$json|ConvertFrom-Json; $bad[2].Partitions[0].TrxHash=(Get-FileHash $path).Hash
        Assert-Rejected {Assert-DistributedVerification $imported $bad $jobs $root} 'outcome'
    }
    [xml]$xml=$original; $rows=@($xml.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $rows[0].testName='Suite.Substituted'
    $xml.Save($path)
    $bad=$json|ConvertFrom-Json; $bad[2].Partitions[0].TrxHash=(Get-FileHash $path).Hash
    Assert-Rejected {Assert-DistributedVerification $imported $bad $jobs $root} 'coverage'
    Write-Host 'Strict distributed aggregation passed.'
}
finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('distributed-aggregate-')) {Remove-Item -LiteralPath $resolved -Recurse -Force}
}
