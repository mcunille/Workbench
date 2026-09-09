[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'scripts/verification-stages.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('workbench-process-test-' + [Guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $fixture 'published'
$processLogRoot = Join-Path $fixture 'logs'
$serverAssembly = Join-Path $publishRoot 'unused.dll'
$baseUrl = 'http://127.0.0.1:1'
$jobs = @()
try {
    New-Item -ItemType Directory -Path $publishRoot, $processLogRoot -Force | Out-Null
    $nativeScript = Join-Path $fixture 'native-output.ps1'
    Set-Content $nativeScript '[Console]::WriteLine("native stdout"); [Console]::Error.WriteLine("native stderr")'
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts/test-publish.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Published verification script does not parse.' }
    foreach ($name in @('publishedProcessParameters', 'probeProcessParameters')) {
        # GIVEN the actual published server/probe launch parameters inside a background job
        $assignment = $ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -eq $name
        }, $true)
        if ($assignment.Count -ne 1) { throw "Expected one launch parameter contract: $name" }
        $parameters = & ([scriptblock]::Create($assignment[0].Right.Extent.Text))
        # Use a real native emitter to exercise the same stdout/stderr inheritance boundary.
        $parameters.FilePath = Join-Path $PSHOME 'pwsh'
        $parameters.ArgumentList = @('-NoProfile', '-File', $nativeScript)
        $parameters.Wait = $true
        if ($IsWindows) { $parameters.WindowStyle = 'Hidden' }
        # WHEN a native descendant writes directly to both OS streams
        $stage = Start-VerificationStage -Name $name -RepositoryRoot $root -Arguments @($parameters) -Action {
            param($launch)
            $process = Start-Process @launch
            if ($process.ExitCode -ne 0) { throw 'Native emitter failed.' }
        }
        $jobs += $stage
        # THEN its bytes cannot corrupt PowerShell's job transport or lose the completion receipt.
        Complete-VerificationStages -Stages @($stage) -RequiredNames @($name)
        foreach ($entry in @(@{ Key='RedirectStandardOutput'; Text='native stdout' }, @{ Key='RedirectStandardError'; Text='native stderr' })) {
            $logPath = $parameters[$entry.Key]
            if (-not $logPath -or -not (Test-Path $logPath) -or (Get-Content $logPath -Raw).Trim() -cne $entry.Text) {
                throw "Published process must capture $($entry.Key) outside the shared published output."
            }
            if ([IO.Path]::GetFullPath((Split-Path $logPath)) -cne [IO.Path]::GetFullPath($processLogRoot)) {
                throw 'Native process logs must not mutate shared release artifacts.'
            }
        }
    }
} finally {
    foreach ($stage in $jobs) { Remove-Job -Job $stage.Job -Force -ErrorAction SilentlyContinue }
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('/','\') -ne [IO.Path]::GetTempPath().TrimEnd('/','\') -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('workbench-process-test-')) { throw 'Unexpected native process test directory.' }
    if (Test-Path $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
Write-Host 'Published native process stream isolation passed.'
