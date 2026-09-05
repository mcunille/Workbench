#Requires -Version 7.0

$ErrorActionPreference = 'Stop'

function Get-Rate([int]$Hit, [int]$Total) {
    if ($Total -eq 0) { return 1.0 }
    return $Hit / $Total
}

function Format-Rate([double]$Rate) {
    return $Rate.ToString('0.####', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Format-Percentage([double]$Percentage) {
    return $Percentage.ToString('0.#', [System.Globalization.CultureInfo]::InvariantCulture)
}

function New-CoverageOutputDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('api', 'web')][string]$Stack,
        [string]$BaseDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'start-refactor-cov')
    )

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $unique = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $path = Join-Path $BaseDirectory "$Stack-$stamp-$unique"
    New-Item -ItemType Directory -Path $path | Out-Null
    return (Resolve-Path -LiteralPath $path).Path
}

function Get-BranchIdentity(
    [string]$Module,
    [string]$Class,
    [string]$Method,
    [hashtable]$Branch
) {
    # Coverlet's native CoverageResult.Merge scopes branches to module/document/class/method, then
    # identifies one branch by these five fields. Preserve that exact identity while the two JSON
    # reports still contain it; Cobertura's per-line fraction cannot reconstruct it afterward.
    return [string]::Join([char]0x1f, [string[]]@(
        $Module,
        $Class,
        $Method,
        [string]$Branch.Line,
        [string]$Branch.Offset,
        [string]$Branch.EndOffset,
        [string]$Branch.Path,
        [string]$Branch.Ordinal
    ))
}

function Merge-CoverletJsonReports {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateCount(1, 100)][string[]]$ReportPath,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $files = @{}

    foreach ($path in $ReportPath) {
        $resolved = (Resolve-Path -LiteralPath $path).Path
        $modules = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json -AsHashtable

        foreach ($moduleEntry in $modules.GetEnumerator()) {
            $moduleName = [string]$moduleEntry.Key
            foreach ($documentEntry in $moduleEntry.Value.GetEnumerator()) {
                $document = [string]$documentEntry.Key
                if (-not $files.ContainsKey($document)) {
                    $files[$document] = [pscustomobject]@{
                        Lines = @{}
                        Branches = @{}
                    }
                }
                $file = $files[$document]

                foreach ($classEntry in $documentEntry.Value.GetEnumerator()) {
                    $className = [string]$classEntry.Key
                    foreach ($methodEntry in $classEntry.Value.GetEnumerator()) {
                        $methodName = [string]$methodEntry.Key
                        $method = $methodEntry.Value

                        foreach ($lineEntry in $method.Lines.GetEnumerator()) {
                            $line = [int]$lineEntry.Key
                            $hits = [int]$lineEntry.Value
                            if (-not $file.Lines.ContainsKey($line) -or $hits -gt $file.Lines[$line]) {
                                # A source line can be listed by the source method, an async state
                                # machine, and closures. It is one physical line: covered by any
                                # listing, but never added to the denominator more than once.
                                $file.Lines[$line] = $hits
                            }
                        }

                        foreach ($branch in @($method.Branches)) {
                            $line = [int]$branch.Line
                            if (-not $file.Branches.ContainsKey($line)) {
                                # C# and Coverlet scope names are case-sensitive. A normal
                                # PowerShell hashtable is not, so it would collapse otherwise
                                # distinct identities such as Foo::Run and foo::Run.
                                $file.Branches[$line] = [System.Collections.Generic.Dictionary[string, int]]::new(
                                    [System.StringComparer]::Ordinal)
                            }
                            $identity = Get-BranchIdentity $moduleName $className $methodName $branch
                            if (-not $file.Branches[$line].ContainsKey($identity)) {
                                $file.Branches[$line][$identity] = 0
                            }
                            # Native Coverlet merging sums hits for the same branch. The gate only
                            # needs hit/non-hit, but retaining the sum keeps the merged report honest.
                            $file.Branches[$line][$identity] += [int]$branch.Hits
                        }
                    }
                }
            }
        }
    }

    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

    $linesTotal = 0
    $linesHit = 0
    $branchesTotal = 0
    $branchesHit = 0
    foreach ($file in $files.Values) {
        $linesTotal += $file.Lines.Count
        $linesHit += @($file.Lines.Values | Where-Object { $_ -gt 0 }).Count
        foreach ($branches in $file.Branches.Values) {
            $branchesTotal += $branches.Count
            $branchesHit += @($branches.Values | Where-Object { $_ -gt 0 }).Count
        }
    }

    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.Xml.XmlWriter]::Create($OutputPath, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('coverage')
        $writer.WriteAttributeString('line-rate', (Format-Rate (Get-Rate $linesHit $linesTotal)))
        $writer.WriteAttributeString('branch-rate', (Format-Rate (Get-Rate $branchesHit $branchesTotal)))
        $writer.WriteAttributeString('lines-covered', [string]$linesHit)
        $writer.WriteAttributeString('lines-valid', [string]$linesTotal)
        $writer.WriteAttributeString('branches-covered', [string]$branchesHit)
        $writer.WriteAttributeString('branches-valid', [string]$branchesTotal)
        $writer.WriteAttributeString('complexity', '0')
        $writer.WriteAttributeString('version', 'coverlet-json-union')
        $writer.WriteAttributeString('timestamp', [string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds())

        $writer.WriteStartElement('sources')
        $writer.WriteElementString('source', '')
        $writer.WriteEndElement()

        $writer.WriteStartElement('packages')
        $writer.WriteStartElement('package')
        $writer.WriteAttributeString('name', 'Workbench server coverage')
        $writer.WriteAttributeString('line-rate', (Format-Rate (Get-Rate $linesHit $linesTotal)))
        $writer.WriteAttributeString('branch-rate', (Format-Rate (Get-Rate $branchesHit $branchesTotal)))
        $writer.WriteAttributeString('complexity', '0')
        $writer.WriteStartElement('classes')

        foreach ($document in $files.Keys | Sort-Object) {
            $file = $files[$document]
            $fileLinesTotal = $file.Lines.Count
            $fileLinesHit = @($file.Lines.Values | Where-Object { $_ -gt 0 }).Count
            $fileBranchesTotal = 0
            $fileBranchesHit = 0
            foreach ($branches in $file.Branches.Values) {
                $fileBranchesTotal += $branches.Count
                $fileBranchesHit += @($branches.Values | Where-Object { $_ -gt 0 }).Count
            }

            $writer.WriteStartElement('class')
            $writer.WriteAttributeString('name', [System.IO.Path]::GetFileNameWithoutExtension($document))
            $writer.WriteAttributeString('filename', $document)
            $writer.WriteAttributeString('line-rate', (Format-Rate (Get-Rate $fileLinesHit $fileLinesTotal)))
            $writer.WriteAttributeString('branch-rate', (Format-Rate (Get-Rate $fileBranchesHit $fileBranchesTotal)))
            $writer.WriteAttributeString('complexity', '0')
            $writer.WriteStartElement('methods')
            $writer.WriteEndElement()
            $writer.WriteStartElement('lines')

            foreach ($line in $file.Lines.Keys | Sort-Object) {
                $writer.WriteStartElement('line')
                $writer.WriteAttributeString('number', [string]$line)
                $writer.WriteAttributeString('hits', [string]$file.Lines[$line])

                $branches = if ($file.Branches.ContainsKey($line)) { $file.Branches[$line] } else { $null }
                if ($branches -and $branches.Count -gt 0) {
                    $hit = @($branches.Values | Where-Object { $_ -gt 0 }).Count
                    $total = $branches.Count
                    $percent = 100 * $hit / $total
                    $writer.WriteAttributeString('branch', 'true')
                    $writer.WriteAttributeString('condition-coverage', "$(Format-Percentage $percent)% ($hit/$total)")
                    $writer.WriteStartElement('conditions')
                    $conditionNumber = 0
                    foreach ($branchHits in $branches.Values) {
                        $writer.WriteStartElement('condition')
                        $writer.WriteAttributeString('number', [string]$conditionNumber)
                        $writer.WriteAttributeString('type', 'jump')
                        $writer.WriteAttributeString('coverage', $(if ($branchHits -gt 0) { '100%' } else { '0%' }))
                        $writer.WriteEndElement()
                        $conditionNumber++
                    }
                    $writer.WriteEndElement()
                }
                else {
                    $writer.WriteAttributeString('branch', 'false')
                }
                $writer.WriteEndElement()
            }

            $writer.WriteEndElement()
            $writer.WriteEndElement()
        }

        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }

    return (Resolve-Path -LiteralPath $OutputPath).Path
}

function Invoke-ApiCoverageProjects {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [scriptblock]$TestExecutor
    )

    $projects = @(
        [pscustomobject]@{
            Name = 'Workbench.Server.IntegrationTests'
            Path = 'tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj'
            DockerRequired = $true
        }
    )
    $reports = [System.Collections.Generic.List[string]]::new()

    foreach ($project in $projects) {
        $resultsDirectory = Join-Path $OutputDirectory $project.Name
        New-Item -ItemType Directory -Force -Path $resultsDirectory | Out-Null
        $projectPath = Join-Path $RepoRoot $project.Path
        Write-Host "Running $($project.Name) with fresh Coverlet JSON coverage..." -ForegroundColor Cyan

        if ($TestExecutor) {
            $execution = @(& $TestExecutor $project.Name $projectPath $resultsDirectory) | Select-Object -Last 1
            $exitCode = [int]$execution.ExitCode
            $report = $execution.ReportPath
        }
        else {
            # No --no-build: each suite must compile the current source before its coverage run.
            & dotnet test $projectPath '--collect:XPlat Code Coverage;Format=json' --results-directory $resultsDirectory | Out-Host
            $exitCode = $LASTEXITCODE
            $report = $null
        }

        if ($exitCode -ne 0) {
            $message = "$($project.Name) failed (exit $exitCode). Coverage is meaningless on a red suite — fix the suite first."
            if ($project.DockerRequired) {
                $message += ' Ensure Docker is running and Testcontainers can start SQL Server.'
            }
            throw $message
        }

        if (-not $report) {
            $report = Get-ChildItem -LiteralPath $resultsDirectory -Recurse -Filter 'coverage.json' |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not $report -or -not (Test-Path -LiteralPath $report)) {
            throw "$($project.Name) passed but produced no Coverlet JSON report under '$resultsDirectory'."
        }

        $resolvedReport = (Resolve-Path -LiteralPath $report).Path
        $reports.Add($resolvedReport)
        Write-Host "Coverage contributor $($project.Name): $resolvedReport"
    }

    return $reports.ToArray()
}

function Get-ApiMergedCoverageReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [scriptblock]$TestExecutor
    )

    $reports = @(Invoke-ApiCoverageProjects `
        -RepoRoot $RepoRoot `
        -OutputDirectory $OutputDirectory `
        -TestExecutor $TestExecutor)
    $mergedReport = Join-Path $OutputDirectory 'coverage.merged.cobertura.xml'
    Merge-CoverletJsonReports -ReportPath $reports -OutputPath $mergedReport | Out-Null
    $resolved = (Resolve-Path -LiteralPath $mergedReport).Path
    Write-Host "Merged API coverage report: $resolved" -ForegroundColor Cyan
    return $resolved
}

Export-ModuleMember -Function New-CoverageOutputDirectory, Merge-CoverletJsonReports, Invoke-ApiCoverageProjects, Get-ApiMergedCoverageReport
