param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path $Workspace).Path
# Install and restore both revisions before either timed canonical gate.
foreach ($phase in @('before','after')) {
    Push-Location (Join-Path $workspaceRoot $phase)
    try {
        dotnet restore Workbench.slnx --locked-mode
        if ($LASTEXITCODE) { throw "$phase locked restore failed." }
        npm ci --prefix src/Workbench.Client --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE) { throw "$phase client install failed." }
        npm ci --prefix tests/Workbench.BrowserTests --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE) { throw "$phase browser install failed." }
    }
    finally { Pop-Location }
}
Push-Location (Join-Path $workspaceRoot 'before')
try {
    npm exec --prefix tests/Workbench.BrowserTests -- playwright install --with-deps chromium
    if ($LASTEXITCODE) { throw 'Chromium install failed.' }
}
finally { Pop-Location }
docker pull mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04
if ($LASTEXITCODE) { throw 'SQL image preparation failed.' }
lscpu | Set-Content (Join-Path $workspaceRoot 'runner-cpu.txt')
$outcomes = @()
foreach ($phase in @('before','after')) {
    Push-Location (Join-Path $workspaceRoot $phase)
    try {
        $revision = (git rev-parse HEAD).Trim()
        $expected = if ($phase -eq 'before') { '35adcd568a4c846b54d7cd996c8c0d575ed93b98' } else { 'fca470a6a2ec29f69c5fbd1934829a576eff3893' }
        if ($revision -ne $expected) { throw 'Unexpected source revision.' }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $succeeded = $false
        try {
            ./scripts/verify.ps1 -SkipDependencyInstall
            $succeeded = $true
        }
        catch { Write-Warning "Canonical $phase failed: $($_.Exception.Message)" }
        $outcomes += [pscustomobject]@{Phase=$phase;Revision=$revision;Succeeded=$succeeded;Seconds=$clock.Elapsed.TotalSeconds}
        $outcomes | ConvertTo-Json | Set-Content (Join-Path $workspaceRoot 'canonical-pair.json')
    }
    finally { Pop-Location }
}
if (@($outcomes | Where-Object { -not $_.Succeeded }).Count) { throw 'A canonical gate failed; this pair cannot establish acceptance.' }
