$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$document = Get-Content -LiteralPath (Join-Path $root '.agents/skills/review-pr/references/github-operations.md') -Raw
$section = ($document -split '## Fetch and select the review boundary')[1] -split '## Publish only approved material' | Select-Object -First 1
$code = [regex]::Match($section, '(?s)```powershell\r?\n(.*?)```').Groups[1].Value

# GIVEN provider-controlled branch names in the review mechanics
# WHEN inspecting the executable boundary
# THEN no branch or anchor is substituted into PowerShell source
if ($code -match '<(?:base|head|anchor)>') { throw 'Unsafe provider placeholder in executable review boundary' }
$boundary = [scriptblock]::Create($code)
$diffCode = [regex]::Matches($section, '(?s)```powershell\r?\n(.*?)```')[1].Groups[1].Value
$diffBoundary = [scriptblock]::Create($diffCode)
function Invoke-TestGit {
    param([string[]]$Arguments)
    $result = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git command failed' }
    return $result
}
function Assert-Rejected {
    param([scriptblock]$Action, [string]$Expected)
    try { & $Action } catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        return
    }
    throw "Expected rejection: $Expected"
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('review-boundary-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
Push-Location $fixture
try {
    # Reftable supports valid Git refs containing double quotes on Windows filesystems.
    Invoke-TestGit @('init', '--quiet', '--ref-format=reftable', 'remote') | Out-Null
    Set-Location remote
    Set-Content -LiteralPath sample.txt -Value before
    Invoke-TestGit @('add', '--', 'sample.txt') | Out-Null
    Invoke-TestGit @('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '--quiet', '--allow-empty', '-m', 'base') | Out-Null
    $base = Invoke-TestGit @('rev-parse', 'HEAD')
    Set-Content -LiteralPath sample.txt -Value after
    Invoke-TestGit @('add', '--', 'sample.txt') | Out-Null
    Invoke-TestGit @('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '--quiet', '--allow-empty', '-m', 'head') | Out-Null
    $head = Invoke-TestGit @('rev-parse', 'HEAD')
    Invoke-TestGit @('update-ref', 'refs/pull/1/head', $head) | Out-Null
    Set-Location ..
    Invoke-TestGit @('init', '--quiet', '--ref-format=reftable', 'review') | Out-Null
    Set-Location review
    Invoke-TestGit @('remote', 'add', 'origin', (Join-Path $fixture 'remote')) | Out-Null

    # GIVEN ordinary and valid metacharacter refs acquired as data
    $branches = @('main', 'semi;colon', 'dollar$(Set-Content${IFS}PWNED${IFS}yes)', "apostrophe'branch", 'paren(branch)', 'double"quote', '-leading')
    foreach ($branch in $branches) {
        Invoke-TestGit @('-C', (Join-Path $fixture 'remote'), 'update-ref', "refs/heads/$branch", $base) | Out-Null
        $pr = [pscustomobject]@{ number = 1; baseRefName = $branch; headRefName = $branch; headRefOid = $head }
        $anchor = $base
        # WHEN fetching the review boundary through real native Git
        . $boundary
        # THEN exact refs and the immutable head are retained, with an ancestral delta
        if ($baseSha -cne $base -or $headSha -cne $head -or $deltaAnchor -cne $base) { throw 'Wrong review boundary' }
        if (Test-Path -LiteralPath PWNED) { throw 'Provider data executed' }
        # AND both eligible delta and full comparisons show the intended file change
        foreach ($deltaAnchor in @($base, $null)) {
            $actualDiff = @(. $diffBoundary)
            if ($actualDiff -cnotcontains '-before' -or $actualDiff -cnotcontains '+after') { throw 'Wrong review diff' }
        }
    }
    # GIVEN no usable anchor (including a missing object or a nonancestor)
    # WHEN selecting the boundary THEN fall back to the full diff
    foreach ($anchor in @($null, ('a' * 40))) {
        . $boundary
        if ($null -ne $deltaAnchor) { throw 'Unusable anchor accepted' }
    }
    $tree = Invoke-TestGit @('-C', (Join-Path $fixture 'remote'), 'rev-parse', 'HEAD^{tree}')
    $orphan = Invoke-TestGit @('-C', (Join-Path $fixture 'remote'), '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit-tree', $tree, '-m', 'orphan')
    Invoke-TestGit @('fetch', '--quiet', 'origin', $orphan) | Out-Null
    $anchor = $orphan
    . $boundary
    if ($null -ne $deltaAnchor) { throw 'Nonancestor accepted' }

    # GIVEN malformed metadata, stale head, or an unavailable ref
    # WHEN selecting the boundary THEN stop before reviewing a different object
    $anchor = 'HEAD;bad'
    Assert-Rejected { . $boundary } 'Invalid review anchor'
    $anchor = $null
    $pr.headRefOid = $base
    Assert-Rejected { . $boundary } 'head changed'
    $pr.headRefOid = 'HEAD'
    Assert-Rejected { . $boundary } 'Invalid PR head SHA'
    $pr.headRefOid = $head
    $pr.baseRefName = 'bad..ref'
    Assert-Rejected { . $boundary } 'Invalid PR branch ref'
    $pr.baseRefName = 'missing'
    Assert-Rejected { . $boundary } 'fetch failed'
    $pr.baseRefName = 'main'
    $pr.number = '1;bad'
    Assert-Rejected { . $boundary } 'Invalid PR number'
    $pr.number = 2
    Assert-Rejected { . $boundary } 'fetch failed'
    Write-Output 'PASS: review boundary literal refs, head identity, anchors, and failure cases'
} finally {
    Pop-Location
    # Keep the isolated fixture for failure diagnosis; it contains no credentials.
}
