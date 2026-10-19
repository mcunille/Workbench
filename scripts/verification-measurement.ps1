. (Join-Path $PSScriptRoot 'server-partition-contract.ps1')

function ConvertTo-VerificationUtc {
    param([object]$Value)
    if ($null -eq $Value) { throw 'Missing verification timestamp.' }
    if ($Value -is [datetimeoffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [datetime]) { return ([datetimeoffset]$Value).ToUniversalTime() }
    if ([string]$Value -notmatch '(Z|[+-][0-9]{2}:[0-9]{2})$') { throw 'Verification timestamps require explicit UTC offsets.' }
    [datetimeoffset]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

function Measure-VerificationGraph {
    param([object]$Run)
    if ($Run.Kind -cnotin @('Baseline','Candidate') -or $Run.Revision -cnotmatch '^[a-f0-9]{40}$' -or
        $Run.SourceHash -cnotmatch '^[a-f0-9]{64}$' -or $Run.EnvironmentHash -cnotmatch '^[a-f0-9]{64}$' -or
        [string]$Run.RunId -notmatch '^[0-9]+$' -or [string]$Run.RunAttempt -notmatch '^[1-9][0-9]*$' -or
        $Run.EvidenceUrl -cne "https://github.com/mcunille/Workbench/actions/runs/$($Run.RunId)") { throw 'Invalid frozen verification provenance.' }
    $required=@('verify','local-setup-contracts','deployment-contracts','container-smoke','required-gate','design-tooling-windows','design-tooling-linux')
    if ($Run.Kind -ceq 'Candidate') { $required+=@('prepare','release','server-a','server-b') }
    Assert-ServerTestCoverage $required @($Run.Jobs.Id)
    $created=ConvertTo-VerificationUtc $Run.CreatedUtc
    $event=ConvertTo-VerificationUtc $Run.EventUtc
    if ($event -gt $created) { throw 'Workflow event is later than its creation.' }
    $finished=$created; $minutes=0.0
    foreach ($job in $Run.Jobs) {
        if ($job.Conclusion -cne 'success') { throw "Verification job $($job.Id) did not succeed." }
        $eligible=ConvertTo-VerificationUtc $job.EligibleUtc
        $started=ConvertTo-VerificationUtc $job.StartedUtc
        $completed=ConvertTo-VerificationUtc $job.CompletedUtc
        if ($eligible -lt $created -or $started -lt $eligible -or $completed -lt $started) { throw 'Invalid verification job timeline.' }
        if ($completed -gt $finished) {$finished=$completed}
        $minutes+=($completed-$started).TotalMinutes
    }
    $requiredFinished=ConvertTo-VerificationUtc @($Run.Jobs | Where-Object Id -CEQ 'required-gate')[0].CompletedUtc
    [pscustomobject]@{FullSeconds=($finished-$created).TotalSeconds; EventToRequiredSeconds=($requiredFinished-$event).TotalSeconds; RunnerMinutes=$minutes}
}

function Compare-DistributedVerificationMeasurement {
    param([object[]]$Rounds,[object]$CoverageBefore,[object]$CoverageAfter)
    $savings=[Collections.Generic.List[double]]::new()
    $costs=[Collections.Generic.List[object]]::new()
    $changes=[Collections.Generic.List[object]]::new()
    $urls=[Collections.Generic.List[string]]::new()
    $failures=[Collections.Generic.List[string]]::new()
    try {
        if ($Rounds.Count -ne 2 -or $Rounds[0].Order -cne 'BaselineThenCandidate' -or $Rounds[1].Order -cne 'CandidateThenBaseline') { throw 'Two predeclared reversed-order rounds are required.' }
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        for ($i=0; $i -lt 2; $i++) {
            $round=$Rounds[$i]
            if ($round.Baseline.Kind -cne 'Baseline' -or $round.Candidate.Kind -cne 'Candidate') { throw 'Measurement arms are reversed.' }
            foreach ($kind in @('Baseline','Candidate')) {
                $run=$round.$kind
                if (-not $seen.Add([string]$run.RunId)) { throw 'Replayed workflow run evidence.' }
                foreach ($field in @('Revision','SourceHash','EnvironmentHash')) {
                    if ($run.$field -cne $Rounds[0].$kind.$field) { throw "Frozen $kind $field changed between rounds." }
                }
                $urls.Add($run.EvidenceUrl)
            }
            if ($round.Baseline.EnvironmentHash -cne $round.Candidate.EnvironmentHash) { throw 'Runner, toolchain, SQL image or dependency/cache policy differs.' }
            $beforeCreated=ConvertTo-VerificationUtc $round.Baseline.CreatedUtc
            $afterCreated=ConvertTo-VerificationUtc $round.Candidate.CreatedUtc
            if (($i -eq 0 -and $beforeCreated -ge $afterCreated) -or ($i -eq 1 -and $afterCreated -ge $beforeCreated)) { throw 'Recorded launch order contradicts workflow timestamps.' }
            $baselineNames=[Collections.Generic.HashSet[string]]::new([string[]]$round.Baseline.Inventory,[StringComparer]::Ordinal)
            $candidateNames=[Collections.Generic.HashSet[string]]::new([string[]]$round.Candidate.Inventory,[StringComparer]::Ordinal)
            if (-not $baselineNames.Count -or $baselineNames.Count -ne $round.Baseline.Inventory.Count -or $candidateNames.Count -ne $round.Candidate.Inventory.Count) { throw 'Empty or duplicate inventory.' }
            $expectedNames=[Collections.Generic.HashSet[string]]::new($baselineNames,[StringComparer]::Ordinal)
            foreach ($name in @(
                'Workbench.Server.IntegrationTests.PurchaseRecognitionIsolationTests.PreparedInputsKeepFinancialStateAndAuthenticationIndependent',
                'Workbench.Server.IntegrationTests.SupplierInputIsolationTests.UnpostedInputsKeepPaymentAndCorrectionAuthoritySeparate'
            )) { if (-not $expectedNames.Add($name)) { throw 'Unexpected isolation case in original baseline.' } }
            if (-not $expectedNames.SetEquals($candidateNames)) { throw 'Unexpected baseline/candidate test inventory delta.' }
            Assert-ServerTestCoverage @($Rounds[0].Baseline.Inventory) @($round.Baseline.Inventory)
            Assert-ServerTestCoverage @($Rounds[0].Candidate.Inventory) @($round.Candidate.Inventory)
            $before=Measure-VerificationGraph $round.Baseline
            $after=Measure-VerificationGraph $round.Candidate
            $saving=$before.FullSeconds-$after.FullSeconds
            $savings.Add($saving)
            foreach ($pair in @(@('Baseline',$before),@('Candidate',$after))) {
                $costs.Add([pscustomobject]@{Round=$i+1;Kind=$pair[0];Minutes=$pair[1].RunnerMinutes;EventToRequiredSeconds=$pair[1].EventToRequiredSeconds;FullSeconds=$pair[1].FullSeconds})
            }
            if ($saving -lt 300) { throw "Round $($i+1) saved $saving seconds; at least 300 are required." }
        }
        if ($CoverageBefore.InstrumentationHash -cnotmatch '^[a-f0-9]{64}$' -or $CoverageBefore.InstrumentationHash -cne $CoverageAfter.InstrumentationHash) { throw 'Coverage instrumentation changed.' }
        if (-not $CoverageBefore.LoadedModules.Count) { throw 'Missing loaded coverage modules.' }
        Assert-ServerTestCoverage $CoverageBefore.LoadedModules $CoverageAfter.LoadedModules
        $metrics=@('server.lines','server.branches','client.lines','client.branches','client.functions','client.statements')
        Assert-ServerTestCoverage $metrics @($CoverageBefore.Metrics.Keys)
        Assert-ServerTestCoverage $metrics @($CoverageAfter.Metrics.Keys)
        foreach ($name in $metrics) {
            $before=$CoverageBefore.Metrics[$name]; $after=$CoverageAfter.Metrics[$name]
            foreach ($metric in @($before,$after)) {
                foreach ($field in @('Covered','Total')) {
                    $value=$metric.$field
                    if ($null -eq $value -or $value -is [string] -or $value -is [bool] -or -not [double]::IsFinite([double]$value) -or $value -lt 0 -or [Math]::Truncate([double]$value) -ne $value) { throw 'Invalid raw coverage counts.' }
                }
                if ($metric.Total -le 0 -or $metric.Covered -gt $metric.Total) { throw 'Invalid raw coverage denominator.' }
            }
            $old=[decimal]$before.Covered/[decimal]$before.Total
            $new=[decimal]$after.Covered/[decimal]$after.Total
            $pp=[Math]::Abs($new-$old)*100
            $relative=if ($old -eq 0) { if ($new -eq 0) {0.0} else {[double]::PositiveInfinity} } else {[Math]::Abs($new-$old)/$old*100}
            $changes.Add([pscustomobject]@{Metric=$name;Before=$before;After=$after;PercentagePoints=$pp;RelativePercent=$relative})
            if ($pp -gt 2 -or $relative -gt 2) { throw "Coverage change exceeds the approved limit: $name" }
        }
    }
    catch { $failures.Add($_.Exception.Message) }
    [pscustomobject]@{Succeeded=($failures.Count -eq 0);RoundSavingsSeconds=$savings.ToArray();CoverageChanges=$changes.ToArray();RunnerMinutes=$costs.ToArray();Failures=$failures.ToArray();EvidenceUrls=$urls.ToArray()}
}
