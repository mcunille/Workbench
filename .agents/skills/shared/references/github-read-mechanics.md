# Shared GitHub read and argument mechanics

These mechanics grant no authority. Reviewer and author skills retain separate permission,
publication, fetch and push contracts. Read the role-specific reference before any write.

## Native arguments

Use PowerShell 7.3 or newer and Standard native argument passing. Treat provider values and local
remote names as untrusted data: retain them in variables, validate full refs with
`git check-ref-format`, and invoke native commands with argument arrays. Never paste those values
into generated commands, interpolated script source or `Invoke-Expression`. Argument-array values
are not recursively parsed as PowerShell source. Do not validate in one passing mode and use another.
Role-specific boundary and push procedures contain executable preconditions.

## Read PR identity

Use `gh pr view <n> --json ...` with the fields required by the role-specific procedure. Check the
native exit code and reject missing metadata. Record `headRefOid`; refresh before publication and
apply that skill's changed-head rule. `gh pr checks <n>` supplies evidence to inspect, not a
substitute for feasible repository verification.

## Read and join feedback

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
  $variables = @('-F', 'owner=<owner>', '-F', 'repo=<repo>', '-F', 'pr=<n>')
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


The example paginates the thread connection. If a thread has more than 100 comments, paginate its
comments connection too before treating that thread as completely read. Collect every REST page;
join GraphQL `databaseId` to the REST comment ID. A recap of thread dispositions is not a new finding,
but claims labeled **Unanchorable findings** are findings even without an inline endpoint.

For multiline bodies use structured serialization, or an exact UTF-8 body file with `--body-file`
when the GitHub CLI command supports it. Read back approved writes against the exact preview.
Publication order, comment prefixes, verdict shape and thread-resolution authority remain in the
separate [reviewer](../../review-pr/references/github-operations.md) and
[author](../../handle-pr-feedback/references/github-operations.md) references.
