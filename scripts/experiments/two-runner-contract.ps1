# Throwaway CI feasibility probe. Never substitutes for the required full gate.
function Get-TwoRunnerProbePartitions {
    param([object[]]$Partitions, [ValidateRange(1, 2)][int]$HostId)
    if ($Partitions.Count -ne 4 -or (($Partitions.Id | Sort-Object) -join ',') -cne '1,2,3,4') {
        throw 'Probe requires four distinct cohorts.'
    }
    return @($Partitions | Where-Object { [int]$_.Id -in @((2 * $HostId - 1), (2 * $HostId)) })
}

function Assert-TwoRunnerProbeEvidence {
    param([string[]]$Roots, [string]$ExpectedHead)
    if ($Roots.Count -ne 2) { throw 'Probe requires two host artifacts.' }
    $manifests = @($Roots | ForEach-Object { Get-Content (Join-Path $_ 'probe.json') -Raw | ConvertFrom-Json })
    if ((($manifests.HostId | Sort-Object) -join ',') -cne '1,2') { throw 'Probe host identities differ.' }
    foreach ($manifest in $manifests) {
        if ($manifest.HeadRevision -cne $ExpectedHead -or $manifest.CheckoutRevision -cne $manifests[0].CheckoutRevision) {
            throw 'Probe source revision differs.'
        }
        if ($manifest.Succeeded -ne $true) { throw 'Probe host did not succeed.' }
    }
    $datasets = @($Roots | ForEach-Object { Get-Content (Join-Path $_ 'duration-dataset.json') -Raw | ConvertFrom-Json })
    if ($datasets[0].Sha256 -cnotmatch '^[a-f0-9]{64}$' -or $datasets[0].Sha256 -cne $datasets[1].Sha256) {
        throw 'Probe duration dataset differs.'
    }
    $reference = $null
    $cases = @()
    for ($index = 0; $index -lt 2; $index++) {
        $inventory = @(Get-Content (Join-Path $Roots[$index] 'inventory.json') -Raw | ConvertFrom-Json)
        $canonical = $inventory | ConvertTo-Json -Depth 6 -Compress
        if ($index -eq 0) { $reference = $canonical; $cases = @($inventory | ForEach-Object { $_.Tests }) }
        elseif ($canonical -cne $reference) { throw 'Probe global inventory differs.' }
        $assigned = @(Get-TwoRunnerProbePartitions $inventory $manifests[$index].HostId)
        foreach ($partition in $assigned) {
            $folder = Join-Path $Roots[$index] "partition-$($partition.Id)"
            $timing = Get-Content (Join-Path $folder 'timing.json') -Raw | ConvertFrom-Json
            Assert-ServerPartitionResult $partition (Join-Path $folder 'results.trx') $timing.ExitCode
        }
        $folders = @(Get-ChildItem -LiteralPath $Roots[$index] -Directory -Filter 'partition-*')
        if ($folders.Count -ne 2) { throw 'Probe contains unexpected partition evidence.' }
    }
    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $cases) { if (-not $unique.Add($name)) { throw 'Probe inventory contains duplicate cases.' } }
    [pscustomobject]@{ Cases = $cases.Count; HeadRevision = $ExpectedHead; CheckoutRevision = $manifests[0].CheckoutRevision; DurationSha256 = $datasets[0].Sha256 }
}
