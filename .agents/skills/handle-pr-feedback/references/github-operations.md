# GitHub operations for `handle-pr-feedback`

Replace placeholders with observed values. These commands describe provider
mechanics, not authorization. Do each external write only after the exact
preview has fresh approval and the PR head has been re-read.

## Read PR metadata and join feedback

Read the live head identity before local edits and again before publishing:

```powershell
gh pr view <n> --json number,title,url,state,headRefName,headRefOid,baseRefName
git branch --show-current
git rev-parse HEAD
```

Read inline REST comments, review records, and top-level PR comments with
pagination. REST provides bodies, database IDs, and original/current anchors.

```powershell
gh api "repos/<owner>/<repo>/pulls/<n>/comments" --paginate
gh api "repos/<owner>/<repo>/pulls/<n>/reviews" --paginate
gh pr view <n> --comments
```

Read GraphQL `reviewThreads` for the node ID required for resolution and for
the authoritative `isResolved` and `isOutdated` state:

```powershell
gh api graphql -F owner='<owner>' -F repo='<repo>' -F pr=<n> -f query='query($owner:String!,$repo:String!,$pr:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$pr){reviewThreads(first:100){nodes{id isResolved isOutdated path line comments(first:100){nodes{databaseId body author{login}}}}}}}}'
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

## Publish approved actions in dependency order

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
   resolve at this point. Do not resolve merely outdated, contested, or
   declined-suggestion threads; those remain open for reviewer response.

```powershell
gh api graphql -f threadId='PRRT_...' -f query='mutation($threadId:ID!){resolveReviewThread(input:{threadId:$threadId}){thread{isResolved}}}'
```

After each approved sequence, read back created issue numbers, posted comments,
and thread state to confirm they match the approved preview one-for-one.
