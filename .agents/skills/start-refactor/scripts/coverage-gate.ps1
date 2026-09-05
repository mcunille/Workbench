#Requires -Version 7.0
<#
.SYNOPSIS
  Enforce 95% line and branch coverage independently on each refactored source file.
.DESCRIPTION
  api builds Workbench.Server.IntegrationTests with Coverlet JSON and converts it to Cobertura.
  web runs the Workbench client suite once with the installed Vitest v8 provider.
  Missing tools or reports fail; the script does not install dependencies.
.PARAMETER Target
  Case-insensitive filename/path fragments. Use commas for multiple files under pwsh -File.
  Every fragment must match measured source; use sufficiently specific paths to identify the unit.
.PARAMETER Stack
  api for Workbench.Server/Workbench.Database; web for Workbench.Client.
.PARAMETER Threshold
  Minimum line AND branch percentage per file. Default and minimum: 95.
.PARAMETER SkipRun
  Evaluate a previously successful run's report. Requires ReportPath. The caller must verify
  the report was produced from the current source and tests; this mode cannot establish freshness.
.PARAMETER ReportPath
  Cobertura file; requires SkipRun.
#>
[CmdletBinding()]
param(
    # Only Target and Stack are positional. Threshold must be named: left implicitly positional, a
    # stray third argument binds to it silently, so `-Stack api -Target Foo.cs 70` would run a 70%
    # gate with no -Threshold visible anywhere on the command line — the exact outcome the SKILL.md
    # red flag about lowering the threshold exists to prevent.
    [Parameter(Mandatory = $true, Position = 0)][string[]]$Target,
    [Parameter(Mandatory = $true, Position = 1)][ValidateSet('api', 'web')][string]$Stack,
    # 95 is the floor the skill documents; this refuses to express anything weaker. Declaring no
    # Position above is what keeps it named-only; ValidateRange is what enforces the floor.
    [ValidateRange(95, 100)][double]$Threshold = 95,
    [switch]$SkipRun,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'

# `pwsh -File` passes arguments as literal strings without parsing PowerShell array syntax, so
# `-Target Bar.cs,Foo.cs` arrives as ONE element "Bar.cs,Foo.cs" and matches nothing. Splitting here
# makes the comma form work under both -File and -Command. Without this, the multi-file case (a
# refactor that split one file into siblings — a multi-file refactor) reports the no-match
# FAIL, which reads as "wrong filename" and pushes toward re-running with a single -Target,
# silently dropping the gate on every other file in the split.
$Target = @($Target | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Target.Count -eq 0) { throw "-Target resolved to nothing after splitting. Pass at least one filename." }

# -ReportPath is only meaningful with -SkipRun. Silently ignoring it would spend several minutes
# regenerating a report and then print numbers from a DIFFERENT file than the one named, with no
# sign the requested report was never opened.
if ($ReportPath -and -not $SkipRun) {
    throw "-ReportPath requires -SkipRun. Without it the suite is re-run and a fresh report is used, so '$ReportPath' would be ignored."
}

# Resolve the repo root from this script's location so the gate works from any working directory.
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')).Path
Import-Module (Join-Path $PSScriptRoot 'coverage-gate-core.psm1') -Force

function Normalize([string]$p) { $p.Replace('\', '/').ToLowerInvariant() }

# ---------------------------------------------------------------- run the suite

if ($SkipRun) {
    if (-not $ReportPath) { throw "-SkipRun requires -ReportPath." }
    $report = (Resolve-Path $ReportPath).Path
}
else {
    $outDir = New-CoverageOutputDirectory -Stack $Stack

    if ($Stack -eq 'api') {
        $report = Get-ApiMergedCoverageReport -RepoRoot $RepoRoot -OutputDirectory $outDir
    }
    else {
        Write-Host "Running vitest with coverage..." -ForegroundColor Cyan
        Push-Location (Join-Path $RepoRoot 'src/Workbench.Client')
        try {
            if (-not (Test-Path -LiteralPath 'node_modules/@vitest/coverage-v8/package.json')) {
                throw 'Client coverage requires an installed @vitest/coverage-v8 matching Vitest. Report this limitation; dependency setup is separate from a behavior-preserving refactor.'
            }
            npm run test:run -- --coverage --coverage.provider=v8 '--coverage.include=src/**/*.{ts,tsx}' --coverage.reporter=cobertura --coverage.reportsDirectory=$outDir
            if ($LASTEXITCODE -ne 0) {
                throw "vitest failed (exit $LASTEXITCODE). Coverage is meaningless on a red suite — fix the suite first."
            }
        }
        finally { Pop-Location }
        $report = Get-ChildItem -Path $outDir -Recurse -Filter 'cobertura-coverage.xml' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
}

if (-not $report -or -not (Test-Path $report)) {
    throw "No Cobertura report was produced. Expected one under the results directory above."
}
Write-Host "Report: $report`n"

# ---------------------------------------------------------------- aggregate per target file

[xml]$xml = Get-Content -LiteralPath $report
# The @() is load-bearing, not decoration: a one-element pipeline unrolls to a bare [String], and
# then $needles[$i] in the unmatched-target check below indexes a CHARACTER out of it rather than
# returning the needle. .Contains([char]) resolves to a different overload that matches almost any
# path, which would turn the typo guard into a rubber stamp for single-target runs.
$needles = @($Target | ForEach-Object { Normalize $_ })

# A single source file is split across several <class> nodes (partial classes, nested types, and
# especially compiler-generated closure/async state-machine classes), and those nodes RE-LIST the
# same physical source lines. Summing <line> nodes directly therefore double-counts: it inflated
# one 1200-line endpoint file to a reported 2520 lines and printed every uncovered line twice.
# So key by line NUMBER per file and merge, taking the best-known hits and branch data for each.
$files = @{}
foreach ($class in $xml.SelectNodes('//class')) {
    $fn = $class.filename
    if (-not $fn) { continue }
    $norm = Normalize $fn
    if (-not ($needles | Where-Object { $norm.Contains($_) })) { continue }

    if (-not $files.ContainsKey($fn)) {
        $files[$fn] = [pscustomobject]@{ File = $fn; Lines = @{} }
    }
    $lines = $files[$fn].Lines

    foreach ($line in $class.SelectNodes('.//line')) {
        $num = [int]$line.number
        $hits = [int]$line.hits

        if (-not $lines.ContainsKey($num)) {
            $lines[$num] = [pscustomobject]@{ Hits = 0; BranchHit = 0; BranchTotal = 0 }
        }
        $slot = $lines[$num]

        # A line covered via ANY class node is covered, so keep the highest hit count seen.
        if ($hits -gt $slot.Hits) { $slot.Hits = $hits }

        # condition-coverage looks like: "50% (1/2)" — only present on branching lines.
        $cc = $line.'condition-coverage'
        if ($cc -and $cc -match '\((\d+)/(\d+)\)') {
            $bHit = [int]$Matches[1]; $bTotal = [int]$Matches[2]
            # Prefer the node reporting the most branches for this line (the fullest view of it),
            # and among equal totals the one showing the most branches actually taken.
            if ($bTotal -gt $slot.BranchTotal -or ($bTotal -eq $slot.BranchTotal -and $bHit -gt $slot.BranchHit)) {
                $slot.BranchHit = $bHit; $slot.BranchTotal = $bTotal
            }
        }
    }
}

# ---------------------------------------------------------------- report + verdict

# A gate that passes because it measured nothing is worse than no gate at all: a typo in -Target
# would otherwise read as a clean sheet. Treat "no match" as a hard failure, never as 100%.
#
# Checked PER TARGET, not just when nothing matched at all. With several targets, one typo among
# otherwise-good names would otherwise be dropped in silence and the run reported as a PASS — and
# that is exactly the shape of the case multi-target exists for: split a file into siblings, misspell
# one of them, and the gate would green-light a refactor having measured only half the split.
# Reported using the caller's original spelling, not the normalized needle — this message is about
# a suspected typo, so echoing it back lowercased would just add a second thing to second-guess.
$unmatched = @(0..($needles.Count - 1) | Where-Object {
        $needle = $needles[$_]
        -not ($files.Keys | Where-Object { (Normalize $_).Contains($needle) })
    } | ForEach-Object { $Target[$_] })
if ($unmatched.Count -gt 0) {
    Write-Host "COVERAGE GATE: FAIL" -ForegroundColor Red
    Write-Host "No file in the report matched: $($unmatched -join ', ')"
    Write-Host "Nothing was measured for those targets, so this is not a pass. Check the spelling"
    Write-Host "against the filenames in the report, and confirm the suite reaches them at all."
    if ($files.Count -gt 0) {
        Write-Host "(Measured, but not enough on its own: $(($files.Keys | Sort-Object) -join ', '))" -ForegroundColor Yellow
    }
    exit 1
}

$failed = $false
foreach ($acc in $files.Values | Sort-Object File) {
    $linesTotal = $acc.Lines.Count
    $linesHit = ($acc.Lines.Values | Where-Object { $_.Hits -gt 0 }).Count
    $uncovered = $acc.Lines.Keys | Where-Object { $acc.Lines[$_].Hits -eq 0 } | Sort-Object
    $branchHit = ($acc.Lines.Values | Measure-Object -Property BranchHit -Sum).Sum
    $branchTotal = ($acc.Lines.Values | Measure-Object -Property BranchTotal -Sum).Sum

    $lineRate = if ($linesTotal) { 100 * $linesHit / $linesTotal } else { 0 }
    # A file with no branches at all vacuously satisfies the branch bar; it never fails on 0/0.
    $hasBranches = $branchTotal -gt 0
    $branchRate = if ($hasBranches) { 100 * $branchHit / $branchTotal } else { 100 }

    $lineOk = $lineRate -ge $Threshold
    $branchOk = $branchRate -ge $Threshold
    if (-not ($lineOk -and $branchOk)) { $failed = $true }

    Write-Host $acc.File -ForegroundColor White
    $lineColor = if ($lineOk) { 'Green' } else { 'Red' }
    Write-Host ("  lines    {0,6:N1}%  ({1}/{2})" -f $lineRate, $linesHit, $linesTotal) -ForegroundColor $lineColor
    if ($hasBranches) {
        $branchColor = if ($branchOk) { 'Green' } else { 'Red' }
        Write-Host ("  branches {0,6:N1}%  ({1}/{2})" -f $branchRate, $branchHit, $branchTotal) -ForegroundColor $branchColor
    }
    else {
        Write-Host "  branches      -   (no branches in this file)" -ForegroundColor DarkGray
    }
    $uncoveredCount = @($uncovered).Count
    if ($uncoveredCount -gt 0) {
        $shown = (@($uncovered) | Select-Object -First 40) -join ', '
        $more = if ($uncoveredCount -gt 40) { " (+$($uncoveredCount - 40) more)" } else { '' }
        Write-Host "  uncovered lines: $shown$more" -ForegroundColor Yellow
    }
    Write-Host ''
}

if ($failed) {
    Write-Host "COVERAGE GATE: FAIL (threshold $Threshold% on both lines and branches)" -ForegroundColor Red
    Write-Host ''
    Write-Host "Each uncovered line is a behavior the safety net does not pin. Add characterization"
    Write-Host "tests for those paths, then verify that meaningful mutations are detected before"
    Write-Host "re-running. A test added to move this number that has never been observed failing is"
    Write-Host "coverage theatre: it executes the line without asserting anything about it."
    exit 1
}

Write-Host "COVERAGE GATE: PASS (>= $Threshold% lines and branches on every target file)" -ForegroundColor Green
exit 0
