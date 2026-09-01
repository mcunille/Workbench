# GitHub operations for `handle-pr-feedback`

Replace placeholders with observed values. These commands describe provider
mechanics, not authorization. The workflow invocation authorizes verified
in-scope edits, commits, and the explicit safe-target push below. Do each
collaboration write only after the exact preview has fresh approval and the PR
head has been re-read.

## Require argv-preserving PowerShell

Run this precondition before any native Git command in the workflow. Windows
PowerShell 5.1 can strip embedded double quotes while splatting native argument
arrays, changing a valid ref into a different ref. Fail closed unless the host
supports and uses the argv-preserving mode:

```powershell
if ($PSVersionTable.PSVersion -lt [version]'7.3') {
  throw 'PowerShell 7.3 or newer is required for exact native Git arguments'
}
$PSNativeCommandArgumentPassing = 'Standard'
if ($PSNativeCommandArgumentPassing -cne 'Standard') {
  throw 'Native argument passing must remain in Standard mode'
}
```

Do not continue under Windows PowerShell 5.1, and do not validate under one
argument-passing mode and push under another.

## Read PR metadata and join feedback

Read the live head identity before local edits and again before publishing:

```powershell
$pr = gh pr view <n> --json number,title,url,state,headRefName,headRefOid,headRepositoryOwner,headRepository,isCrossRepository,baseRefName | ConvertFrom-Json
$headOwner = $pr.headRepositoryOwner.login
$headRepo = $pr.headRepository.name
$headBranch = [string]$pr.headRefName
$headRef = "refs/heads/$headBranch"
$checkRefArgs = @('check-ref-format', $headRef)
& git @checkRefArgs
if ($LASTEXITCODE -ne 0) { throw "Invalid PR head ref" }
git branch --show-current
git rev-parse HEAD
```

Record the initial `headRefOid`. Before editing, require local `HEAD` to equal
that SHA and require either the named PR head branch or a documented detached
PR-head checkout. Do not assume `origin` owns the PR branch: for a fork,
`origin` commonly identifies the base repository.

## Verify and use the push target

Before push, inventory local remote names and every configured push URL as data.
Verify that every push URL on the selected remote identifies the observed head
repository, including equivalent HTTPS and SSH URL forms. Select exactly
one inventory entry through data input; do not type a remote name into generated
PowerShell source.

```powershell
$remoteNames = @(& git remote)
$remoteInventory = @(
  foreach ($candidate in $remoteNames) {
    $getUrlArgs = @('remote', 'get-url', '--push', '--all', '--', [string]$candidate)
    $pushUrls = @(& git @getUrlArgs)
    if ($LASTEXITCODE -ne 0) { throw "Cannot read a remote push URL" }
    [pscustomobject]@{ Name = [string]$candidate; PushUrls = [string[]]$pushUrls }
  }
)

$headNameWithOwner = "$headOwner/$headRepo"
$repoViewArgs = @('repo', 'view', $headNameWithOwner, '--json', 'nameWithOwner,url,sshUrl')
$headRepoMetadata = & gh @repoViewArgs | ConvertFrom-Json
$remoteInventory
$headRepoMetadata

$selectedRemote = Read-Host 'Enter the verified head remote name from the inventory'
$remoteMatches = @($remoteInventory | Where-Object { $_.Name -ceq $selectedRemote })
if ($remoteMatches.Count -ne 1) { throw "Select exactly one inventoried remote" }
$headRemote = [string]$remoteMatches[0].Name
```

Re-read PR metadata immediately before push and require its `headRefOid` to
still equal the recorded initial SHA. Then use an explicit remote and full
refspec, whether the checkout is attached, fork-based, or detached:

```powershell
$pushArgs = @('push', '--', $headRemote, "HEAD:$headRef")
& git @pushArgs
if ($LASTEXITCODE -ne 0) { throw "Explicit PR-head push failed" }
gh pr view <n> --json headRefOid
git rev-parse HEAD
```

The post-push `headRefOid` must equal local `HEAD`. Never use bare `git push`,
an implicit upstream, or a remote without the explicit refspec. If no remote's
push URL maps to the discovered head repository, or the mapping is ambiguous,
stop until the correct remote is explicitly named or safely configured. Do not
substitute the base repository or force-push.

Provider values such as `headRefName` and local values such as remote names are
untrusted command data. Never paste provider or local values into command
source, an interpolated script, `Invoke-Expression`, or a generated shell
command. Keep them in variables from acquisition through use, validate the full
`refs/heads/` ref with `git check-ref-format`, and invoke Git with argument
arrays. PowerShell does not recursively parse values splatted from an array, so
characters such as `$()`, semicolons, and apostrophes remain literal data.

Read inline REST comments, review records, and top-level PR comments with
pagination. REST provides bodies, database IDs, and original/current anchors.

```powershell
gh api "repos/<owner>/<repo>/pulls/<n>/comments" --paginate
gh api "repos/<owner>/<repo>/pulls/<n>/reviews" --paginate
gh api "repos/<owner>/<repo>/issues/<n>/comments" --paginate
```

Read GraphQL `reviewThreads` for the node ID required for resolution and for
the authoritative `isResolved` and `isOutdated` state. Follow the connection's
cursor until `pageInfo.hasNextPage` is false; `first:100` alone is not an
inventory of all unresolved threads:

```powershell
$query = 'query($owner:String!,$repo:String!,$pr:Int!,$cursor:String){repository(owner:$owner,name:$repo){pullRequest(number:$pr){reviewThreads(first:100,after:$cursor){nodes{id isResolved isOutdated path line comments(first:100){nodes{databaseId body author{login}}}} pageInfo{hasNextPage endCursor}}}}}'
$cursor = $null
do {
  $variables = @('-F', 'owner=<owner>', '-F', 'repo=<repo>', '-F', 'pr=<n>')
  if ($null -ne $cursor) { $variables += @('-F', "cursor=$cursor") }
  $page = gh api graphql @variables -f query=$query | ConvertFrom-Json
  $threads = $page.data.repository.pullRequest.reviewThreads
  $threads.nodes
  $cursor = $threads.pageInfo.endCursor
} while ($threads.pageInfo.hasNextPage)
```

Join GraphQL comment `databaseId` to the REST comment ID. REST does not expose
thread resolution; GraphQL does not reliably preserve a usable current anchor.
An outdated GraphQL thread may have `line: null`; recover its context from the
REST `original_line`, `original_commit_id`, and path. `isOutdated` does not
mean `isResolved`.

Read review bodies as well as threads. A verdict recap that only summarizes
thread dispositions is not a finding. A claim under a clearly labeled
**Unanchorable findings** section is a finding even though it has no thread;
include it in triage and answer it in a top-level PR comment.

## Publish approved collaboration actions in dependency order

Each body begins once, and only once, with `AI: `. Serialize multi-line payloads
programmatically rather than hand-writing JSON.

1. Create each approved deferred issue first, with repository-required labels;
   later replies can then cite its number.

```powershell
gh issue create --title '<approved title>' --body 'AI: <approved body>' --label <approved-label>
```

2. Post each approved inline reply to its REST comment database ID. Replies
   should cite a pushed commit, test evidence, or created issue.

```powershell
$reply = @{ body = 'AI: <approved reply>' } | ConvertTo-Json -Compress
$reply | gh api "repos/<owner>/<repo>/pulls/<n>/comments/<comment-id>/replies" --method POST --input -
```

3. Post a separate approved top-level reply for every unanchorable finding,
   because it has no inline comment endpoint. Then post exactly one approved
   concise round summary describing fixes, deferrals, and answered-open items.

```powershell
gh pr comment <n> --body 'AI: <approved unanchorable-finding reply>'
gh pr comment <n> --body 'AI: <approved round summary>'
```

4. Resolve only an approved thread that is genuinely complete: fixed,
   already handled with visible evidence, or reviewer-conceded. The author can
   resolve at this point. A verified fixed thread remains eligible even when
   its anchor is outdated. Do not resolve a thread merely because it is
   outdated, or when it is contested or a declined suggestion; those remain
   open for reviewer response.

```powershell
gh api graphql -f threadId='PRRT_...' -f query='mutation($threadId:ID!){resolveReviewThread(input:{threadId:$threadId}){thread{isResolved}}}'
```

After each approved sequence, read back created issue numbers, posted comments,
and thread state to confirm they match the approved preview one-for-one.
