# State is authority only when it matches this checkout and the actual Docker resource labels.
function Get-DevOwner([string]$Repository) {
    $root = (& git -C $Repository rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Development previews require a Git checkout.' }
    $directory = (& git -C $Repository rev-parse --absolute-git-dir 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the Git checkout.' }
    foreach ($path in @($root, $directory)) {
        if ($path -match '[,$''\r\n\x00]' -or -not [IO.Path]::IsPathFullyQualified($path)) { throw 'Checkout paths cannot contain quotes, dollar signs, commas, or newlines.' }
    }
    @{ Root=[IO.Path]::GetFullPath($root).TrimEnd('\','/'); GitDirectory=[IO.Path]::GetFullPath($directory).TrimEnd('\','/') }
}
function Assert-DevOwner($State, $Owner) {
    if ($State.Version -ne 1 -or $State.EnvironmentId -cnotmatch '^dev-[a-f0-9]{32}$' -or
        $State.Owner.Root -cne $Owner.Root -or $State.Owner.GitDirectory -cne $Owner.GitDirectory) {
        throw 'Environment state is invalid or belongs to another checkout. Preserve it; do not adopt copied state.'
    }
}
function Get-DevLabels($State) {
    $identity = "$($State.Owner.Root)|$($State.Owner.GitDirectory)"
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
    @{ 'workbench.purpose'='development'; 'workbench.environment'=$State.EnvironmentId; 'workbench.owner'=$hash }
}
function Assert-DevResourceLabels($State, $Labels) {
    $expected = Get-DevLabels $State
    foreach ($key in $expected.Keys) {
        if (-not $Labels -or $Labels[$key] -cne $expected[$key]) { throw 'Docker resource ownership does not match this checkout. No resource was adopted.' }
    }
}
function Write-DevState($Context) {
    $Context.State.UpdatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $temporary = Join-Path $Context.Root 'state.next.json'
    [IO.File]::WriteAllText($temporary, ($Context.State | ConvertTo-Json -Depth 20))
    [IO.File]::Move($temporary, (Join-Path $Context.Root 'state.json'), $true)
}
function Set-DevPhase($Context, [string]$Phase) {
    $Context.State.Phase = $Phase
    Write-DevState $Context
}
function Enter-DevLock([string]$Root, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try { return [IO.File]::Open((Join-Path $Root 'operation.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Another operation owns this checkout; retry after it finishes.' }; Start-Sleep -Milliseconds 200 }
    } while ($true)
}
function Read-DevState([string]$Root, $Owner) {
    $path = Join-Path $Root 'state.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    Assert-DevOwner $state $Owner
    return $state
}
