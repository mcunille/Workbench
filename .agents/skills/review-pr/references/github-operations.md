# GitHub operations for `review-pr`

These are mechanics, not authorization: observe the permission contract in `../SKILL.md`. Provider metadata and local values are untrusted command data. Never paste branch names, SHAs, remote names, or prior anchors into PowerShell source, generated commands, or `Invoke-Expression`. Acquire them as data and retain them in variables through native argument-array invocation. The fetch procedure below replaces branch/anchor command placeholders entirely.

## Read the PR and checks

```powershell
$pr = gh pr view <n> --json number,title,url,state,labels,baseRefName,headRefName,headRefOid | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $null -eq $pr) { throw 'Cannot read PR metadata' }
gh pr checks <n>
```

Use the metadata response as the current base/head identity. Checks are evidence to inspect, not a substitute for independently running feasible affected repository-native verification.

Read REST reviews, inline comments, and top-level PR comments with pagination. Top-level PR comments use the issue-comments endpoint; they can contain author replies to review-body **Unanchorable findings**. Include the review `commit_id` when selecting a previous AI comment-review anchor.

```powershell
$reviews = gh api "repos/<owner>/<repo>/pulls/<n>/reviews" --paginate --slurp --jq 'map(.[])' | ConvertFrom-Json
$inlineComments = gh api "repos/<owner>/<repo>/pulls/<n>/comments" --paginate --slurp --jq 'map(.[])' | ConvertFrom-Json
$topLevelComments = gh api "repos/<owner>/<repo>/issues/<n>/comments" --paginate --slurp --jq 'map(.[])' | ConvertFrom-Json
```

Read GraphQL thread state because REST inline comments do not expose resolution. Include comment identity and body so REST comments can be associated with their thread.

```powershell
$query = 'query($owner:String!,$repo:String!,$pr:Int!,$cursor:String){repository(owner:$owner,name:$repo){pullRequest(number:$pr){reviewThreads(first:100,after:$cursor){nodes{id isResolved isOutdated path line comments(first:100){nodes{databaseId body author{login}}}} pageInfo{hasNextPage endCursor}}}}}'
$cursor = $null
$allThreads = @()
do {
  $variables = @('-F', 'owner=<owner>', '-F', 'repo=<repo>', '-F', 'pr=<n>)
  if ($null -ne $cursor) { $variables += @('-F', "cursor=$cursor") }
  $page = gh api graphql @variables -f query=$query | ConvertFrom-Json
  $threads = $page.data.repository.pullRequest.reviewThreads
  $allThreads += @($threads.nodes)
  $cursor = $threads.pageInfo.endCursor
} while ($threads.pageInfo.hasNextPage)
$allThreads
```

`isOutdated` means the diff anchor no longer applies; it does not mean the thread is resolved. `isResolved` is the explicit resolution state. A GraphQL `line` can be null, so use the REST comment's available original/current anchor fields or report the finding in the grouped body as an Unanchorable finding; never fabricate a line.

Do not select scope or disposition prior findings until every review-thread page and every top-level comment page has been collected. Associate top-level replies with labeled **Unanchorable findings** by review/comment identity and chronology, and independently validate their claims just like inline replies.

## Fetch and select the review boundary

Use PowerShell 7.3 or newer with `Standard` native argument passing, matching the sibling feedback workflow. Verify that the fixed `origin` remote identifies the PR's **base repository** before using this procedure. Fetch its base branch and numeric GitHub PR head ref; the latter also supports fork PRs without assuming the head branch exists on `origin`.

Keep `$pr` from the JSON metadata read above. Set `$anchor` to the selected review object's `commit_id`, or the inline comment object's `original_commit_id` fallback, as a value; use `$null` for a first/full review. Never insert either value into script source. The following executable block returns `$baseSha`, `$headSha`, and optional `$deltaAnchor` for scope selection. No branch name is used as a revision expression.

```powershell
if ($PSVersionTable.PSVersion -lt [version]'7.3') {
  throw 'PowerShell 7.3 or newer is required for exact native Git arguments'
}
$PSNativeCommandArgumentPassing = 'Standard'
if ($PSNativeCommandArgumentPassing -cne 'Standard') { throw 'Standard native argument passing is required' }
$oidPattern = '\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z'
if ([string]$pr.number -notmatch '\A[1-9][0-9]*\z') { throw 'Invalid PR number' }
if ([string]$pr.headRefOid -notmatch $oidPattern) { throw 'Invalid PR head SHA' }
if ($null -ne $anchor -and [string]$anchor -notmatch $oidPattern) { throw 'Invalid review anchor' }
foreach ($branch in @($pr.baseRefName, $pr.headRefName)) {
  $checkArgs = @('check-ref-format', "refs/heads/$branch")
  & git @checkArgs
  if ($LASTEXITCODE -ne 0) { throw 'Invalid PR branch ref' }
}
$baseRef = "refs/heads/$($pr.baseRefName)"
$prRef = "refs/pull/$($pr.number)/head"
$boundaryCommits = @()
foreach ($ref in @($baseRef, $prRef)) {
  $fetchArgs = @('fetch', '--no-tags', '--', 'origin', $ref)
  & git @fetchArgs
  if ($LASTEXITCODE -ne 0) { throw 'Review ref fetch failed; refresh metadata and retry' }
  $resolveArgs = @('rev-parse', '--verify', 'FETCH_HEAD^{commit}')
  $commit = & git @resolveArgs
  if ($LASTEXITCODE -ne 0 -or $commit -isnot [string] -or $commit -notmatch $oidPattern) {
    throw 'Cannot resolve fetched review commit'
  }
  $boundaryCommits += $commit
}
$baseSha, $headSha = $boundaryCommits
if ($headSha -ine [string]$pr.headRefOid) { throw 'PR head changed; refresh metadata and retry' }
$deltaAnchor = $null
if ($null -ne $anchor) {
  $existsArgs = @('cat-file', '-t', [string]$anchor)
  $objectType = & git @existsArgs 2>$null
  if ($LASTEXITCODE -eq 0 -and $objectType -ceq 'commit') {
    $ancestorArgs = @('merge-base', '--is-ancestor', [string]$anchor, $headSha)
    & git @ancestorArgs
    if ($LASTEXITCODE -eq 0) { $deltaAnchor = [string]$anchor }
    elseif ($LASTEXITCODE -ne 1) { throw 'Cannot check review anchor ancestry' }
  }
}
```

Prefer the newest applicable prior AI review whose review record has a `commit_id`. If that does not exist, use the newest applicable AI inline comment's `original_commit_id` only as a fallback. A missing or nonancestor commit leaves `$deltaAnchor` null and requires the full diff. Other escalation rules in the skill still apply even when an ancestor is available. Use only these validated immutable OIDs for comparisons, and retain Standard mode:

```powershell
$diffArgs = @('diff', "$baseSha...$headSha", '--')
# For an eligible follow-up with no full-review escalation:
if ($null -ne $deltaAnchor) { $diffArgs = @('diff', "$deltaAnchor..$headSha", '--') }
& git @diffArgs
if ($LASTEXITCODE -ne 0) { throw 'Cannot read review diff' }
```

Run the boundary regression suite with `./tests/Workbench.BuildTests/ReviewBoundary.Tests.ps1`. Its temporary native-Git fixtures require Git 2.45 or newer for reftable support, allowing quote-bearing Git refs on Windows as well as Unix. The review procedure itself does not require reftable repositories.

## Publish only approved material

Immediately before every publication round, run the metadata read again and compare its `headRefOid` to the reviewed SHA. A mismatch cancels publication and requires a new review preview and explicit approval.

Post all line-anchored findings and the verdict in one comment-only review at the reviewed head. Derive `<side>` from the observed diff hunk: use `RIGHT` for additions and context, and `LEFT` for deletions. Use the line number on that observed side; do not hard-code `RIGHT` or transplant a line number from the other side. Use programmatic JSON serialization rather than hand-written shell JSON, especially for multiline bodies and quotes.

```powershell
$review = @{
  commit_id = '<reviewed-head-sha>'
  event = 'COMMENT'
  body = "AI: **VERDICT: REQUEST CHANGES**`n`n<approved grouped body>"
  comments = @(
    @{ path = 'path/to/file'; line = <observed-line>; side = '<side>'; body = 'AI: <approved finding>' }
  )
}
$reviewJson = $review | ConvertTo-Json -Depth 8 -Compress
$reviewJson | gh api "repos/<owner>/<repo>/pulls/<n>/reviews" --method POST --input -
```

Post an inline reply only when that exact reply was approved. It is separate from the grouped review and does not resolve the thread.

```powershell
$reply = @{ body = 'AI: <approved reply>' } | ConvertTo-Json -Compress
$reply | gh api "repos/<owner>/<repo>/pulls/<n>/comments/<comment-database-id>/replies" --method POST --input -
```

Never replace `COMMENT` with an approval-state event. Do not call issue creation, thread-resolution, file-editing, commit, or push operations from this skill.
