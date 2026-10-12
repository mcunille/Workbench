param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path $Workspace).Path
$expected = 'fca470a6a2ec29f69c5fbd1934829a576eff3893'
foreach ($phase in @('two','four')) {
    Push-Location (Join-Path $workspaceRoot $phase)
    try {
        if ((git rev-parse HEAD).Trim() -ne $expected) { throw 'Unexpected candidate source revision.' }
        dotnet restore Workbench.slnx --locked-mode
        if ($LASTEXITCODE) { throw "$phase locked restore failed." }
        npm ci --prefix src/Workbench.Client --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE) { throw "$phase client install failed." }
        npm ci --prefix tests/Workbench.BrowserTests --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE) { throw "$phase browser install failed." }
    }
    finally { Pop-Location }
}
Push-Location (Join-Path $workspaceRoot 'two')
try {
    npm exec --prefix tests/Workbench.BrowserTests -- playwright install --with-deps chromium
    if ($LASTEXITCODE) { throw 'Chromium install failed.' }
}
finally { Pop-Location }
docker pull mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04
if ($LASTEXITCODE) { throw 'SQL image preparation failed.' }
lscpu | Set-Content (Join-Path $workspaceRoot 'runner-cpu.txt')
Get-Content /proc/meminfo | Set-Content (Join-Path $workspaceRoot 'runner-memory.txt')
$outcomes = @()
foreach ($phase in @('two','four')) {
    $workers = if ($phase -eq 'two') { 2 } else { 4 }
    $leftoverSql = @(docker ps --filter ancestor=mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04 --format '{{.ID}}')
    if ($LASTEXITCODE -or $leftoverSql.Count) { throw 'The phase cannot start with unavailable Docker or a running SQL container from the previous phase.' }
    $resourcePath = Join-Path $workspaceRoot "$phase-resources.jsonl"
    $sampler = Start-Job -ArgumentList $resourcePath -ScriptBlock {
        param($resourcePath)
        while ($true) {
            try {
                $containers = @(docker stats --no-stream --format '{{.ID}}|{{.CPUPerc}}|{{.MemUsage}}|{{.BlockIO}}' 2>&1)
                if ($LASTEXITCODE) { throw 'Docker resource sampling failed.' }
                [pscustomobject]@{
                    Utc = [DateTimeOffset]::UtcNow.ToString('o')
                    Cpu = (Get-Content /proc/stat -TotalCount 1)
                    Memory = @(Get-Content /proc/meminfo | Where-Object { $_ -match '^(MemTotal|MemAvailable|SwapTotal|SwapFree):' })
                    IoPressure = @(Get-Content /proc/pressure/io)
                    CpuPressure = @(Get-Content /proc/pressure/cpu)
                    MemoryPressure = @(Get-Content /proc/pressure/memory)
                    Containers = $containers
                } | ConvertTo-Json -Compress | Add-Content $resourcePath
            }
            catch {
                [pscustomobject]@{Utc=[DateTimeOffset]::UtcNow.ToString('o');SamplingFailure=$_.Exception.Message} | ConvertTo-Json -Compress | Add-Content $resourcePath
            }
            Start-Sleep -Seconds 5
        }
    }
    Push-Location (Join-Path $workspaceRoot $phase)
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $succeeded = $false
        try {
            ./scripts/verify.ps1 -SkipDependencyInstall -ServerPartitions $workers -ServerConcurrency $workers
            $succeeded = $true
        }
        catch { Write-Warning "Canonical $phase failed: $($_.Exception.Message)" }
        $outcomes += [pscustomobject]@{Phase=$phase;Revision=$expected;Workers=$workers;Succeeded=$succeeded;Seconds=$clock.Elapsed.TotalSeconds}
        $outcomes | ConvertTo-Json | Set-Content (Join-Path $workspaceRoot 'scheduling-pair.json')
    }
    finally {
        Pop-Location
        Stop-Job $sampler
        Receive-Job $sampler -ErrorAction Continue
        Remove-Job $sampler
    }
}
if (@($outcomes | Where-Object { -not $_.Succeeded }).Count) { throw 'A canonical gate failed; scheduling acceptance is unavailable.' }
