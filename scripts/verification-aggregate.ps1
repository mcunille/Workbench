. (Join-Path $PSScriptRoot 'verification-transport.ps1')
. (Join-Path $PSScriptRoot 'server-partition-contract.ps1')

function Assert-DistributedVerification {
    param([object]$ImportedTransport, [object[]]$Receipts, [System.Collections.IDictionary]$JobResults, [string]$EvidenceRoot)
    foreach ($job in @('prepare','release','server-a','server-b')) {
        if (-not $JobResults.Contains($job) -or $JobResults[$job].result -cne 'success') { throw "Required distributed job $job did not succeed." }
    }
    $plan=$ImportedTransport.Plan
    Assert-DistributedServerPlan $plan
    if ($Receipts.Count -ne 4) { throw 'Missing or duplicate phase receipt.' }
    $expectedKeys=@('Prepare/','Release/','Server/A','Server/B')
    try { Assert-ServerTestCoverage $expectedKeys @($Receipts | ForEach-Object { "$($_.Phase)/$($_.HostId)" }) }
    catch { throw "Phase receipt completeness failed: $($_.Exception.Message)" }
    # Reject reparse points before reading result files under this untrusted evidence tree.
    $null=@(Get-VerificationTransportFiles $EvidenceRoot)
    $caseNames=[Collections.Generic.List[string]]::new()
    foreach ($receipt in $Receipts) {
        if ($receipt.Version -ne 1 -or $receipt.Succeeded -isnot [bool] -or -not $receipt.Succeeded -or
            $receipt.TransportHash -cne $ImportedTransport.TransportHash -or $receipt.PlanHash -cne $ImportedTransport.PlanHash) { throw 'Invalid distributed phase receipt version, digest or result.' }
        Assert-VerificationTransportContext $receipt.Context $ImportedTransport.Context
        if ($receipt.Seconds -isnot [ValueType] -or -not [double]::IsFinite([double]$receipt.Seconds) -or $receipt.Seconds -lt 0) { throw 'Invalid phase receipt timing.' }
        $expectedStages=switch ($receipt.Phase) { Prepare {@('client')}; Release {@('browser','published')}; Server {@()} }
        Assert-ServerTestCoverage $expectedStages @($receipt.Stages.Stage)
        foreach ($stage in $receipt.Stages) { if ($stage.Succeeded -isnot [bool] -or -not $stage.Succeeded) { throw 'Failed stage receipt.' } }
        if ($receipt.Phase -cne 'Server') {
            if ($receipt.Partitions.Count) { throw 'Unexpected phase partition receipt.' }
            continue
        }
        $expectedIds=if ($receipt.HostId -ceq 'A') {@(1,2)} else {@(3,4)}
        $hostEntry=@($plan.Hosts | Where-Object Id -CEQ $receipt.HostId)
        if ($hostEntry.Count -ne 1) { throw 'Missing partition host assignment.' }
        Assert-ServerTestCoverage @($expectedIds | ForEach-Object {[string]$_}) @($hostEntry[0].PartitionIds | ForEach-Object {[string]$_})
        Assert-ServerTestCoverage @($expectedIds | ForEach-Object {[string]$_}) @($receipt.Partitions.PartitionId | ForEach-Object {[string]$_})
        foreach ($result in $receipt.Partitions) {
            $partition=@($plan.Partitions | Where-Object Id -EQ $result.PartitionId)[0]
            $relative="server-$($receipt.HostId.ToLowerInvariant())/partition-$($partition.Id)/results.trx"
            if ($result.TrxPath -cne $relative) { throw 'Unexpected partition evidence path.' }
            $path=Join-Path $EvidenceRoot $relative
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Missing partition evidence.' }
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $result.TrxHash) { throw 'Partition evidence digest mismatch.' }
            if ($null -eq $result.ExitCode -or $result.ExitCode -is [string]) { throw 'Missing partition exit code.' }
            Assert-ServerPartitionResult -Partition $partition -ResultPath $path -ExitCode $result.ExitCode
            [xml]$trx=Get-Content -LiteralPath $path -Raw
            foreach ($row in $trx.SelectNodes('//*[local-name()="UnitTestResult"]')) { $caseNames.Add($row.testName) }
        }
    }
    Assert-ServerTestCoverage -Expected $plan.Inventory -Actual $caseNames.ToArray()
    [pscustomobject]@{ Succeeded=$true; Cases=$caseNames.Count; Partitions=4; Context=$ImportedTransport.Context; TransportHash=$ImportedTransport.TransportHash; PlanHash=$ImportedTransport.PlanHash }
}
