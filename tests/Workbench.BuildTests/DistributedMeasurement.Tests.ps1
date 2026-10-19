$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot '../../scripts/verification-measurement.ps1'
if (Test-Path $script) {. $script}
if (-not (Get-Command Compare-DistributedVerificationMeasurement -ErrorAction SilentlyContinue)) {throw 'Complete graph accounting behavior is missing.'}
function New-RunFixture([string]$Kind,[string]$Id,[datetime]$Created,[double]$Seconds) {
    $ids=@('verify','local-setup-contracts','deployment-contracts','container-smoke','required-gate','design-tooling-windows','design-tooling-linux')
    if ($Kind -ceq 'Candidate') {$ids+=@('prepare','release','server-a','server-b')}
    $jobs=@(foreach ($idName in $ids) { [pscustomobject]@{Id=$idName;EligibleUtc=$Created.ToString('o');StartedUtc=$Created.AddSeconds(10).ToString('o');CompletedUtc=$Created.AddSeconds($(if ($idName -eq 'required-gate') {$Seconds} else {$Seconds-1})).ToString('o');Conclusion='success'} })
    $inventory=@('Suite.A','Suite.B')
    if ($Kind -ceq 'Candidate') {$inventory+=@('Workbench.Server.IntegrationTests.PurchaseRecognitionIsolationTests.PreparedInputsKeepFinancialStateAndAuthenticationIndependent','Workbench.Server.IntegrationTests.SupplierInputIsolationTests.UnpostedInputsKeepPaymentAndCorrectionAuthoritySeparate')}
    [pscustomobject]@{Kind=$Kind;RunId=$Id;RunAttempt='1';Revision=$(if ($Kind -ceq 'Baseline') {'a'*40} else {'b'*40});SourceHash=$(if ($Kind -ceq 'Baseline') {'a'*64} else {'b'*64});EnvironmentHash=('c'*64);Inventory=$inventory;CreatedUtc=$Created.ToString('o');EventUtc=$Created.AddSeconds(-2).ToString('o');Jobs=$jobs;EvidenceUrl="https://github.com/mcunille/Workbench/actions/runs/$Id"}
}
$start=[datetime]'2026-10-18T00:00:00Z'
$rounds=@(
    [pscustomobject]@{Order='BaselineThenCandidate';Baseline=(New-RunFixture Baseline 1 $start 3000);Candidate=(New-RunFixture Candidate 2 $start.AddHours(2) 2700)},
    [pscustomobject]@{Order='CandidateThenBaseline';Baseline=(New-RunFixture Baseline 4 $start.AddDays(1).AddHours(2) 3000);Candidate=(New-RunFixture Candidate 3 $start.AddDays(1) 2700)}
)
$metrics=@{}
foreach ($name in @('server.lines','server.branches','client.lines','client.branches','client.functions','client.statements')) {$metrics[$name]=@{Covered=90;Total=100}}
$coverage=[pscustomobject]@{InstrumentationHash=('d'*64);LoadedModules=@('Workbench.Server','Workbench.Client');Metrics=$metrics}
function Copy-Fixture($Value) {,$($Value|ConvertTo-Json -Depth 30|ConvertFrom-Json -AsHashtable)}
# GIVEN two complete successful ordered rounds WHEN full graphs save exactly five minutes THEN accept without rounding.
$result=Compare-DistributedVerificationMeasurement $rounds $coverage $coverage
if (-not $result.Succeeded -or ($result.RoundSavingsSeconds -join ',') -ne '300,300' -or $result.RunnerMinutes.Count -ne 4) {throw "Valid boundary rejected: $($result.Failures -join '; ')"}
# GIVEN a late independent design check THEN full CI and event-to-required-gate remain distinct clocks.
$late=Copy-Fixture $rounds
$late[0].Baseline.Jobs | Where-Object Id -eq 'design-tooling-windows' | ForEach-Object {$_.CompletedUtc=$start.AddSeconds(3300).ToString('o')}
$result=Compare-DistributedVerificationMeasurement $late $coverage $coverage
if (-not $result.Succeeded -or $result.RunnerMinutes[0].EventToRequiredSeconds -ne 3002 -or $result.RunnerMinutes[0].FullSeconds -ne 3300) {throw 'Independent completion replaced required-gate timestamp.'}
# GIVEN exactly two percentage points and two percent relative change THEN decimal boundaries are inclusive.
$full=Copy-Fixture $coverage; $lower=Copy-Fixture $coverage
$full.Metrics['server.lines'].Covered=100; $lower.Metrics['server.lines'].Covered=98
if (-not (Compare-DistributedVerificationMeasurement $rounds $full $lower).Succeeded) {throw 'Exact two-percent coverage boundary rejected.'}
# GIVEN a fractional shortfall, a queue delay or a dominating independent job THEN do not substitute inner gate timing.
foreach ($mode in @('fraction','queue','smoke','cancelled','missing','duplicate','replay','order','source','environment','inventory')) {
    $bad=Copy-Fixture $rounds
    switch ($mode) {
        fraction {$bad[0].Candidate.Jobs | Where-Object Id -eq 'required-gate' | ForEach-Object {$_.CompletedUtc=([datetime]$_.CompletedUtc).AddMilliseconds(1).ToString('o')}}
        queue {$bad[0].Candidate.CreatedUtc=([datetime]$bad[0].Candidate.CreatedUtc).AddSeconds(-100).ToString('o');$bad[0].Candidate.EventUtc=$bad[0].Candidate.CreatedUtc}
        smoke {$bad[0].Candidate.Jobs | Where-Object Id -eq 'container-smoke' | ForEach-Object {$_.CompletedUtc=([datetime]$bad[0].Candidate.CreatedUtc).AddSeconds(2800).ToString('o')}}
        cancelled {$bad[0].Candidate.Jobs[0].Conclusion='cancelled'}
        missing {$bad[0].Candidate.Jobs=@($bad[0].Candidate.Jobs | Where-Object Id -ne 'server-b')}
        duplicate {$bad[0].Candidate.Jobs+=@($bad[0].Candidate.Jobs[0])}
        replay {$bad[1].Candidate.RunId=$bad[0].Candidate.RunId}
        order {$bad[1].Order='BaselineThenCandidate'}
        source {$bad[1].Candidate.SourceHash=('e'*64)}
        environment {$bad[1].Candidate.EnvironmentHash=('f'*64)}
        inventory {$bad[0].Candidate.Inventory=@('Suite.Substituted','Suite.B')+$bad[0].Candidate.Inventory[2..3]}
    }
    if ((Compare-DistributedVerificationMeasurement $bad $coverage $coverage).Succeeded) {throw "Accepted invalid timing evidence: $mode"}
}
# GIVEN identical percentage-looking results under different instrumentation THEN fail scope comparisons.
foreach ($mode in @('instrumentation','module','metric','relative','counts')) {
    $changed=Copy-Fixture $coverage
    switch ($mode) {
        instrumentation {$changed.InstrumentationHash=('e'*64)}
        module {$changed.LoadedModules=@('Workbench.Client')}
        metric {$changed.Metrics.Remove('server.branches')}
        relative {$changed.Metrics['server.lines'].Covered=88} # 2pp but 2.22% relative
        counts {$changed.Metrics['server.lines'].Covered=101}
    }
    if ((Compare-DistributedVerificationMeasurement $rounds $coverage $changed).Succeeded) {throw "Accepted invalid coverage evidence: $mode"}
}
Write-Host 'Complete CI timing and coverage accounting passed.'
