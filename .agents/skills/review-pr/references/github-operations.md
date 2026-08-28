# GitHub operations for `review-pr`

Replace `<n>`, `<owner>`, `<repo>`, `<base>`, `<head>`, `<anchor>`, and placeholders with observed values. These are mechanics, not authorization: observe the permission contract in `../SKILL.md`.

## Read the PR and checks

```powershell
gh pr view <n> --json number,title,url,state,labels,baseRefName,headRefName,headRefOid
gh pr checks <n>
```

Use the metadata response as the current base/head identity. Checks are evidence to inspect, not a substitute for independently running feasible affected repository-native verification.

Read REST reviews and inline comments with pagination. Include the review `commit_id` when selecting a previous AI comment-review anchor.

```powershell
gh api "repos/<owner>/<repo>/pulls/<n>/reviews" --paginate
gh api "repos/<owner>/<repo>/pulls/<n>/comments" --paginate
```

Read GraphQL thread state because REST inline comments do not expose resolution. Include comment identity and body so REST comments can be associated with their thread.

```powershell
gh api graphql -F owner='<owner>' -F repo='<repo>' -F pr=<n> -f query='query($owner:String!,$repo:String!,$pr:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$pr){reviewThreads(first:100){nodes{id isResolved isOutdated path line comments(first:100){nodes{databaseId body author{login}}}}}}}}'
```

`isOutdated` means the diff anchor no longer applies; it does not mean the thread is resolved. `isResolved` is the explicit resolution state. A GraphQL `line` can be null, so use the REST comment's available original/current anchor fields or report the finding in the grouped body as an Unanchorable finding; never fabricate a line.

## Fetch and select the review boundary

Fetch both current refs, verify the fetched head SHA against `headRefOid`, then validate a prior anchor before using a delta.

```powershell
git fetch origin <base> <head>
git rev-parse "origin/<base>"
git rev-parse "origin/<head>"
git merge-base --is-ancestor <anchor> "origin/<head>"
git diff "origin/<base>...origin/<head>"
git diff "<anchor>..origin/<head>"
```

Prefer the newest applicable prior AI review whose review record has a `commit_id`. If that does not exist, use the newest applicable AI inline comment's `original_commit_id` only as a fallback. The ancestry command must succeed before the anchored delta is used. Refresh metadata or fetch an explicit PR ref when a fork or timing race makes a named remote head unavailable.

## Publish only approved material

Immediately before every publication round, run the metadata read again and compare its `headRefOid` to the reviewed SHA. A mismatch cancels publication and requires a new review preview and explicit approval.

Post all line-anchored findings and the verdict in one comment-only review at the reviewed head. Use programmatic JSON serialization rather than hand-written shell JSON, especially for multiline bodies and quotes.

```powershell
$review = @{
  commit_id = '<reviewed-head-sha>'
  event = 'COMMENT'
  body = "AI: **VERDICT: REQUEST CHANGES**`n`n<approved grouped body>"
  comments = @(
    @{ path = 'path/to/file'; line = 42; side = 'RIGHT'; body = 'AI: <approved finding>' }
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
