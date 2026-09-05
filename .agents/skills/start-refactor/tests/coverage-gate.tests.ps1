#Requires -Version 7.0

$ErrorActionPreference = 'Stop'

$skillRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$modulePath = Join-Path $skillRoot 'scripts/coverage-gate-core.psm1'
$gatePath = Join-Path $skillRoot 'scripts/coverage-gate.ps1'

Import-Module $modulePath -Force

$script:failures = [System.Collections.Generic.List[string]]::new()

function Assert-Equal($Expected, $Actual, [string]$Because) {
    if ($Expected -ne $Actual) {
        throw "Expected '$Expected', got '$Actual': $Because"
    }
}

function Assert-Matches([string]$Pattern, [string]$Actual, [string]$Because) {
    if ($Actual -notmatch $Pattern) {
        throw "Expected output to match '$Pattern': $Because`nActual:`n$Actual"
    }
}

function Invoke-Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        Write-Host "PASS: $Name" -ForegroundColor Green
    }
    catch {
        $script:failures.Add("${Name}: $($_.Exception.Message)")
        Write-Host "FAIL: $Name" -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
}

function New-Branch(
    [int]$Line,
    [int]$Path,
    [int]$Ordinal,
    [int]$Hits
) {
    [ordered]@{
        Line = $Line
        Offset = 20
        EndOffset = 40
        Path = $Path
        Ordinal = $Ordinal
        Hits = $Hits
    }
}

function New-Method([hashtable]$Lines, [object[]]$Branches = @()) {
    [ordered]@{
        Lines = $Lines
        Branches = @($Branches)
    }
}

function Write-CoverletReport(
    [string]$Path,
    [hashtable]$Classes,
    [string]$Document = 'C:\repo\Target.cs'
) {
    $report = [ordered]@{
        'Workbench.Server.dll' = [ordered]@{
            $Document = $Classes
        }
    }
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "coverage-gate-tests-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    Invoke-Case 'A line hit only by integration tests is covered in the merged report' {
        # GIVEN complementary coverage contributors for one source file
        $unit = Join-Path $testRoot 'unit-line.json'
        $integration = Join-Path $testRoot 'integration-line.json'
        $merged = Join-Path $testRoot 'merged-line.xml'

        Write-CoverletReport $unit @{
            'Target' = @{ 'System.Void Target::Run()' = (New-Method @{ '10' = 0 }) }
        }
        Write-CoverletReport $integration @{
            'Target' = @{ 'System.Void Target::Run()' = (New-Method @{ '10' = 3 }) }
        }

        # WHEN combining coverage with physical line and branch identities
        Merge-CoverletJsonReports -ReportPath @($unit, $integration) -OutputPath $merged

        # THEN the report preserves the measured outcomes
        [xml]$xml = Get-Content -LiteralPath $merged
        $line = $xml.SelectSingleNode("//class[@filename='C:\repo\Target.cs']/lines/line[@number='10']")
        Assert-Equal 3 ([int]$line.hits) 'integration execution must contribute to the union'
    }

    Invoke-Case 'Complementary branches from the two suites merge to two of two' {
        # GIVEN complementary coverage contributors for one source file
        $unit = Join-Path $testRoot 'unit-branches.json'
        $integration = Join-Path $testRoot 'integration-branches.json'
        $merged = Join-Path $testRoot 'merged-branches.xml'

        Write-CoverletReport $unit @{
            'Target' = @{
                'System.Void Target::Run()' = (New-Method @{ '10' = 1 } @(
                    (New-Branch 10 0 0 1),
                    (New-Branch 10 1 1 0)
                ))
            }
        }
        Write-CoverletReport $integration @{
            'Target' = @{
                'System.Void Target::Run()' = (New-Method @{ '10' = 1 } @(
                    (New-Branch 10 0 0 0),
                    (New-Branch 10 1 1 1)
                ))
            }
        }

        # WHEN combining coverage with physical line and branch identities
        Merge-CoverletJsonReports -ReportPath @($unit, $integration) -OutputPath $merged

        # THEN the report preserves the measured outcomes
        [xml]$xml = Get-Content -LiteralPath $merged
        $line = $xml.SelectSingleNode("//class[@filename='C:\repo\Target.cs']/lines/line[@number='10']")
        Assert-Equal '100% (2/2)' ([string]$line.'condition-coverage') 'branch identity must be unioned before Cobertura is generated'
    }

    Invoke-Case 'Case-differing Coverlet scopes remain distinct branches' {
        # GIVEN distinct case-sensitive method scopes
        $report = Join-Path $testRoot 'case-sensitive-scopes.json'
        $merged = Join-Path $testRoot 'merged-case-sensitive-scopes.xml'
        @'
{
  "Workbench.Server.dll": {
    "C:\\repo\\Target.cs": {
      "Target": {
        "System.Void Target::Run()": {
          "Lines": { "10": 1 },
          "Branches": [
            { "Line": 10, "Offset": 20, "EndOffset": 40, "Path": 0, "Ordinal": 0, "Hits": 1 }
          ]
        }
      },
      "target": {
        "System.Void target::Run()": {
          "Lines": { "10": 0 },
          "Branches": [
            { "Line": 10, "Offset": 20, "EndOffset": 40, "Path": 0, "Ordinal": 0, "Hits": 0 }
          ]
        }
      }
    }
  }
}
'@ | Set-Content -LiteralPath $report -Encoding utf8

        # WHEN combining coverage with physical line and branch identities
        Merge-CoverletJsonReports -ReportPath @($report, $report) -OutputPath $merged

        # THEN the report preserves the measured outcomes
        [xml]$xml = Get-Content -LiteralPath $merged
        $coverage = [string]$xml.SelectSingleNode("//line[@number='10']").'condition-coverage'
        Assert-Equal '50% (1/2)' $coverage 'C# and Coverlet scope names are case-sensitive'
    }

    Invoke-Case 'Cobertura percentages use invariant decimal separators' {
        # GIVEN complementary coverage contributors for one source file
        $unit = Join-Path $testRoot 'unit-culture.json'
        $integration = Join-Path $testRoot 'integration-culture.json'
        $merged = Join-Path $testRoot 'merged-culture.xml'

        Write-CoverletReport $unit @{
            'Target' = @{
                'System.Void Target::Run()' = (New-Method @{ '10' = 1 } @(
                    (New-Branch 10 0 0 1),
                    (New-Branch 10 1 1 0),
                    (New-Branch 10 2 2 0)
                ))
            }
        }
        Write-CoverletReport $integration @{
            'Target' = @{
                'System.Void Target::Run()' = (New-Method @{ '10' = 1 } @(
                    (New-Branch 10 0 0 0),
                    (New-Branch 10 1 1 0),
                    (New-Branch 10 2 2 0)
                ))
            }
        }

        $originalCulture = [System.Globalization.CultureInfo]::CurrentCulture
        try {
            [System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo('de-DE')
            # WHEN combining coverage with physical line and branch identities
        Merge-CoverletJsonReports -ReportPath @($unit, $integration) -OutputPath $merged
        }
        finally {
            [System.Globalization.CultureInfo]::CurrentCulture = $originalCulture
        }

        # THEN the report preserves the measured outcomes
        [xml]$xml = Get-Content -LiteralPath $merged
        $coverage = [string]$xml.SelectSingleNode("//line[@number='10']").'condition-coverage'
        Assert-Equal '33.3% (1/3)' $coverage 'Cobertura uses a culture-independent decimal point'
    }

    Invoke-Case 'Duplicate physical line entries are emitted once' {
        # GIVEN complementary coverage contributors for one source file
        $unit = Join-Path $testRoot 'unit-duplicates.json'
        $integration = Join-Path $testRoot 'integration-duplicates.json'
        $merged = Join-Path $testRoot 'merged-duplicates.xml'

        Write-CoverletReport $unit @{
            'Target' = @{ 'System.Void Target::Run()' = (New-Method @{ '10' = 1 }) }
            'Target/<Run>d__0' = @{ 'System.Void Target/<Run>d__0::MoveNext()' = (New-Method @{ '10' = 1 }) }
        }
        Write-CoverletReport $integration @{
            'Target' = @{ 'System.Void Target::Run()' = (New-Method @{ '10' = 1 }) }
        }

        # WHEN combining coverage with physical line and branch identities
        Merge-CoverletJsonReports -ReportPath @($unit, $integration) -OutputPath $merged

        # THEN the report preserves the measured outcomes
        [xml]$xml = Get-Content -LiteralPath $merged
        $lines = @($xml.SelectNodes("//class[@filename='C:\repo\Target.cs']/lines/line[@number='10']"))
        Assert-Equal 1 $lines.Count 'compiler-generated classes must not duplicate a physical source line'
    }

    Invoke-Case 'A missing target fails the gate' {
        # GIVEN complementary coverage contributors for one source file
        $unit = Join-Path $testRoot 'unit-missing.json'
        $integration = Join-Path $testRoot 'integration-missing.json'
        $merged = Join-Path $testRoot 'merged-missing.xml'

        Write-CoverletReport $unit @{
            'Target' = @{ 'System.Void Target::Run()' = (New-Method @{ '10' = 1 }) }
        }
        Write-CoverletReport $integration @{
            'Target' = @{ 'System.Void Target::Run()' = (New-Method @{ '10' = 1 }) }
        }
        # WHEN combining coverage with physical line and branch identities
        Merge-CoverletJsonReports -ReportPath @($unit, $integration) -OutputPath $merged

        # WHEN requesting a filename absent from the measured report
        $output = (& pwsh -NoProfile -File $gatePath -Stack api -Target Missing.cs -SkipRun -ReportPath $merged 2>&1) -join "`n"
        # THEN missing coverage is rejected explicitly
        Assert-Equal 1 $LASTEXITCODE 'measuring no matching file must be a hard failure'
        Assert-Matches 'No file in the report matched: Missing.cs' $output 'the failure must identify the missing target'
    }

    Invoke-Case 'A single Workbench report can be converted' {
        # GIVEN coverage from the only Workbench server test project
        $report = Join-Path $testRoot 'single.json'
        Write-CoverletReport $report @{
            'Target' = @{ 'System.Void Target::Run()' = (New-Method @{ '10' = 1 }) }
        }
        # WHEN converting one contributor
        $merged = Merge-CoverletJsonReports -ReportPath @($report) -OutputPath (Join-Path $testRoot 'single.xml')
        # THEN the measured line remains covered
        # THEN the report preserves the measured outcomes
        [xml]$xml = Get-Content -LiteralPath $merged
        Assert-Equal 1 ([int]$xml.SelectSingleNode('//line').hits) 'single-project coverage must be supported'
    }

    Invoke-Case 'The runner selects the actual Workbench test project once' {
        # GIVEN one successful test executor
        $calls = [System.Collections.Generic.List[string]]::new()
        $report = Join-Path $testRoot 'runner.json'
        Write-CoverletReport $report @{
            'Target' = @{ 'System.Void Target::Run()' = (New-Method @{ '10' = 1 }) }
        }
        $executor = {
            param($Name, $ProjectPath, $ResultsDirectory)
            $calls.Add($ProjectPath)
            [pscustomobject]@{ ExitCode = 0; ReportPath = $report }
        }
        # WHEN collecting server coverage
        $merged = Get-ApiMergedCoverageReport -RepoRoot $testRoot -OutputDirectory (Join-Path $testRoot 'runner') -TestExecutor $executor
        # THEN only the repository's real project contributes
        Assert-Equal 1 $calls.Count 'Workbench has one server test project'
        Assert-Equal (Join-Path $testRoot 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj') $calls[0] 'use the Workbench project'
        Assert-Equal $true (Test-Path -LiteralPath $merged) 'return the converted report'
    }

    Invoke-Case 'A failing server suite aborts without coverage' {
        # GIVEN a failed SQL-backed test run
        $executor = { [pscustomobject]@{ ExitCode = 1; ReportPath = $null } }
        # WHEN collecting coverage
        $message = try {
            Get-ApiMergedCoverageReport -RepoRoot $testRoot -OutputDirectory (Join-Path $testRoot 'failure') -TestExecutor $executor
            'DID NOT THROW'
        } catch { $_.Exception.Message }
        # THEN the failure identifies the actual project and infrastructure
        Assert-Matches 'Workbench.Server.IntegrationTests failed' $message 'name the failed suite'
        Assert-Matches 'SQL Server' $message 'explain the actual database dependency'
    }

    Invoke-Case 'Every requested file must be measured and satisfy both thresholds' {
        # GIVEN a fully covered file and a file with a missing branch
        $report = Join-Path $testRoot 'gate.xml'
        '<coverage><packages><package><classes><class filename="Good.cs"><lines><line number="1" hits="1" /></lines></class><class filename="Branch.cs"><lines><line number="1" hits="1" condition-coverage="50% (1/2)" /></lines></class></classes></package></packages></coverage>' | Set-Content $report
        # WHEN evaluating complete, missing, and under-covered target sets
        foreach ($case in @(
            @{ Target = 'Good.cs'; Exit = 0 },
            @{ Target = 'Good.cs,Missing.cs'; Exit = 1 },
            @{ Target = 'Good.cs,Branch.cs'; Exit = 1 }
        )) {
            $output = (& pwsh -NoProfile -File $gatePath -Stack api -Target $case.Target -SkipRun -ReportPath $report 2>&1) -join "`n"
            # THEN no covered sibling hides a missing file or branch
            Assert-Equal $case.Exit $LASTEXITCODE $output
        }
    }
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTestRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
}
if ($script:failures.Count -gt 0) {
    $script:failures | ForEach-Object { Write-Host $_ }
    exit 1
}
Write-Host 'All coverage-gate tests passed.'
exit 0
