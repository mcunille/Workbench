# The preview build context is an explicit allowlist of Git-visible Dockerfile inputs.
function Get-DevSource([string]$Repository) {
    $paths = @(& git -C $Repository -c core.quotepath=false ls-files --cached --others --exclude-standard 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inventory preview build inputs.' }
    $entries = @(); $files = @()
    foreach ($relative in ($paths | Sort-Object -Unique)) {
        if ($relative -notin @('Dockerfile','.dockerignore','global.json','Directory.Build.props','Directory.Packages.props') -and
            $relative -notmatch '^src/Workbench\.(Server|Database|Client)/') { continue }
        if ($relative -match '(^|/)(bin|obj|dist|node_modules|TestResults|\.dev-environment)(/|$)' -or
            $relative -match '(^|/)\.env($|\.)') { continue }
        if ($relative -match '[\r\n]' -or $relative.StartsWith('"')) { throw 'Unsupported source filename in preview context.' }
        $path = Join-Path $Repository $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Preview build inputs must be regular files.' }
        $files += $relative
        $entries += "$relative=$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)"
    }
    if ($files -cnotcontains 'Dockerfile') { throw 'Preview Dockerfile is missing.' }
    @{ Hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))))
        Files=$files }
}
function Build-DevImage($Context, $Source) {
    Set-DevPhase $Context 'build'
    $snapshot = Join-Path $Context.Root ('build-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $snapshot | Out-Null
    try {
        foreach ($relative in $Source.Files) {
            $target = Join-Path $snapshot $relative
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $Context.Owner.Root $relative) -Destination $target
        }
        # Hash the snapshot with the same relative input ordering, detecting edits during copying.
        $entries = foreach ($relative in $Source.Files) { "$relative=$((Get-FileHash -LiteralPath (Join-Path $snapshot $relative) -Algorithm SHA256).Hash)" }
        $copiedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))))
        if ($copiedHash -cne $Source.Hash -or (Get-DevSource $Context.Owner.Root).Hash -cne $Source.Hash) { throw 'Source changed while capturing the preview; rerun dev-up.' }
        $imageFile = Join-Path $snapshot 'image-id'
        Invoke-DevDocker $Context @('build','--label',"workbench.source=$($Source.Hash)",'--iidfile',$imageFile,$snapshot) | Out-Null
        $image = (Get-Content -LiteralPath $imageFile -Raw).Trim()
        if ($image -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'Build did not produce an immutable image ID.' }
        if ((Get-DevSource $Context.Owner.Root).Hash -cne $Source.Hash) { throw 'Source changed during the preview build; rerun dev-up. Existing preview remains available.' }
        return $image
    } finally {
        $resolved = [IO.Path]::GetFullPath($snapshot)
        if ([IO.Path]::GetDirectoryName($resolved) -cne [IO.Path]::GetFullPath($Context.Root) -or
            [IO.Path]::GetFileName($resolved) -cnotmatch '^build-[a-f0-9]{32}$') { throw 'Unexpected build snapshot path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
