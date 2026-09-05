[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# GIVEN the repository's CI and security analysis workflows
$workflowPaths = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.github/workflows') -File |
    Where-Object { $_.Extension -in '.yml', '.yaml' }
$actionCount = 0
foreach ($workflowPath in $workflowPaths) {
    # WHEN inspecting each action invocation in the repository's block-style YAML
    foreach ($line in Get-Content -LiteralPath $workflowPath.FullName) {
        if ($line -match '^\s*(?:-\s*)?uses\s*:\s*(?<action>.+?)\s*$') {
            $action = $Matches.action
            # THEN executable remote action code is bound to a full commit, with a readable version
            if ($action -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[a-f0-9]{40}\s+#\s+v\d+(?:\.\d+)*\s*$') {
                throw "Action must use a full commit SHA and version comment in $($workflowPath.Name): $action"
            }
            $actionCount++
        }
    }
}
# AND the check must actually inspect the current action inventory
if ($actionCount -lt 9) {
    throw "Expected at least nine action invocations; inspected $actionCount."
}
Write-Host "Workflow action pin verification passed ($actionCount invocations)."
