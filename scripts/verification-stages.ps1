# Shared fail-closed stage accounting for local verification and the required CI gate.
function Assert-RequiredCiJobs {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Needs)
    foreach ($name in @('local-setup-contracts', 'deployment-contracts', 'verify', 'container-smoke')) {
        if (-not $Needs.Contains($name) -or $Needs[$name].result -cne 'success') {
            throw "Required CI job '$name' did not succeed (missing, failed, cancelled, or skipped)."
        }
    }
}

function Start-VerificationStage {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][scriptblock]$Action,
        [object[]]$Arguments = @()
    )
    $job = Start-Job -ScriptBlock {
        param($root, $name, $source, $arguments)
        $ErrorActionPreference = 'Stop'
        Set-Location -LiteralPath $root
        $clock = [Diagnostics.Stopwatch]::StartNew()
        try {
            & ([scriptblock]::Create($source)) @arguments
            [pscustomobject]@{ VerificationStage = $name; Succeeded = $true; Seconds = $clock.Elapsed.TotalSeconds; Failure = '' }
        }
        catch {
            [pscustomobject]@{ VerificationStage = $name; Succeeded = $false; Seconds = $clock.Elapsed.TotalSeconds; Failure = $_.Exception.Message }
        }
    } -ArgumentList $RepositoryRoot, $Name, $Action.ToString(), $Arguments
    [pscustomobject]@{ Name = $Name; Job = $job }
}

function Complete-VerificationStages {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Stages,
        [Parameter(Mandatory)][string[]]$RequiredNames,
        [string]$TimingPath
    )
    $names = @($Stages | ForEach-Object Name)
    if (@($names | Select-Object -Unique).Count -ne $names.Count) { throw 'Duplicate verification stages.' }
    foreach ($name in $RequiredNames) {
        if ($names -cnotcontains $name) { throw "Missing required verification stage '$name'." }
    }
    if (@($names | Where-Object { $RequiredNames -cnotcontains $_ }).Count) { throw 'Unexpected verification stage.' }
    $results = @()
    $failures = @()
    foreach ($stage in $Stages) {
        $stage.Job | Wait-Job | Out-Null
        $output = @(Receive-Job -Job $stage.Job -Keep -ErrorAction Continue)
        $result = @($output | Where-Object { $_.PSObject.Properties['VerificationStage'] })
        $output | Where-Object { -not $_.PSObject.Properties['VerificationStage'] } | ForEach-Object { Write-Host "[$($stage.Name)] $_" }
        if ($stage.Job.State -ne 'Completed' -or $result.Count -ne 1 -or
            $result[0].VerificationStage -cne $stage.Name -or $result[0].Succeeded -ne $true) {
            $failures += $stage.Name
        }
        $results += [pscustomobject]@{
            Stage = $stage.Name
            State = [string]$stage.Job.State
            Succeeded = ($failures -cnotcontains $stage.Name)
            Seconds = if ($result.Count -eq 1) { $result[0].Seconds } else { $null }
            Failure = if ($result.Count -eq 1) { $result[0].Failure } else { 'Missing completion receipt' }
        }
    }
    if ($TimingPath) { $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $TimingPath }
    $results | ForEach-Object { Write-Host ("{0}: success={1}, seconds={2:N2} {3}" -f $_.Stage, $_.Succeeded, $_.Seconds, $_.Failure) }
    if ($failures.Count) { throw "Required verification stages failed: $($failures -join ', ')." }
}
