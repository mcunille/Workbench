# Final review fix report

Date: 2026-08-28

Branch: `codex/pr-review-skills`

Starting commit: `98d2753 Fix PR feedback thread handling`

## Inputs read

- `AGENTS.md`
- `docs/specs/2026-08-28-github-pr-review-skills-design.md`
- `.agents/skills/review-pr/SKILL.md`
- `.agents/skills/review-pr/references/github-operations.md`
- `.agents/skills/handle-pr-feedback/SKILL.md`
- `.agents/skills/handle-pr-feedback/references/github-operations.md`
- `.superpowers/sdd/2026-08-28-github-pr-review-skills/progress.md` (the SDD ledger)

The user-requested auto-execution rule in `AGENTS.md` was not edited.

## Corrections

1. `review-pr` now inventories paginated top-level PR/issue comments and every
   GraphQL `reviewThreads` page before selecting scope or dispositioning prior
   findings. The REST examples collect every page with `--paginate --slurp`,
   and the GraphQL example accumulates every page's nodes in `$allThreads`.
   Follow-up review explicitly validates top-level author replies to labeled
   unanchorable findings.
2. `handle-pr-feedback` now discovers `headRepositoryOwner` and
   `headRepository`, verifies a remote push URL against that identity, and
   requires the explicit non-force refspec
   `HEAD:refs/heads/<headRefName>`. It covers attached, fork-based, and detached
   checkouts, rejects default/upstream push behavior, and verifies the live
   post-push SHA against local `HEAD`.
3. The design and author skill now distinguish authorization categories:
   invoking the author workflow authorizes verified in-scope edits, tests,
   commits, and the explicit safe-target push; exact fresh approval gates only
   collaboration writes (issues, comments/replies/summaries, reviews, and
   thread resolution). Push-before-draft remains mandatory.
4. The reviewer inline payload now takes observed `<side>` and
   `<observed-line>` values. The rule is `RIGHT` for additions/context and
   `LEFT` for deletions; the example no longer hard-codes `RIGHT`.

## RED evidence

Before editing, a direct PowerShell structural harness checked the requested
contracts. Command shape:

```powershell
$reviewSkill = Get-Content -Raw '.agents/skills/review-pr/SKILL.md'
$reviewOps = Get-Content -Raw '.agents/skills/review-pr/references/github-operations.md'
$authorSkill = Get-Content -Raw '.agents/skills/handle-pr-feedback/SKILL.md'
$authorOps = Get-Content -Raw '.agents/skills/handle-pr-feedback/references/github-operations.md'
# Require-Match / Forbid-Match assertions for pagination, push targeting,
# approval categories, observed diff side, and the AGENTS invariant.
```

Output before the correction:

```text
FAIL: review inventory names top-level PR comments
FAIL: review issue comments use paginated REST
FAIL: review GraphQL accepts a cursor
FAIL: review GraphQL exposes pageInfo
FAIL: review GraphQL loops through pages
FAIL: author discovers head repository owner
FAIL: author discovers head repository
FAIL: author push uses explicit remote and refspec
FAIL: author skill distinguishes collaboration writes
FAIL: author skill says invocation authorizes code delivery
FAIL: review inline payload uses observed side placeholder
FAIL: review side rules include both sides
FAIL: review payload does not hard-code RIGHT
```

The first version of the AGENTS assertion compared differently normalized
PowerShell strings and produced a false failure. It was corrected to the VCS
invariant `git diff --quiet HEAD -- AGENTS.md`; that check is green below.

## Direct structural assertions

Command: a PowerShell `Require-Match` / `Forbid-Match` harness over both
entrypoints, both references, the binding design spec, repository state, and
reference existence. It also runs:

```powershell
git diff --quiet HEAD -- AGENTS.md
git ls-files --error-unmatch 'docs/plans/2026-08-28-github-pr-review-skills.md'
```

Final output:

```text
PASS: review skill inventories paginated top-level comments
PASS: review skill inventories every thread page
PASS: review skill validates top-level unanchorable replies
PASS: review operations collect paginated issue comments
PASS: review GraphQL accepts cursor
PASS: review GraphQL returns page info
PASS: review GraphQL accumulates nodes
PASS: review GraphQL follows all pages
PASS: review operations defer scope until collection complete
PASS: review payload uses observed side slot
PASS: review payload side rule covers additions context deletions
PASS: review payload does not hard-code RIGHT
PASS: author skill makes invocation the code-delivery authorization
PASS: author skill limits preview gate to collaboration writes
PASS: author skill preserves push-before-draft
PASS: author skill discovers head owner and repository
PASS: author skill requires explicit remote and refspec
PASS: author skill forbids default push
PASS: author operations query head owner
PASS: author operations query head repository
PASS: author operations verify remote push URL
PASS: author operations inspect canonical head repository identity
PASS: author operations use explicit remote and full refspec
PASS: author operations verify post-push live SHA
PASS: author operations cover fork and detached checkout
PASS: author operations paginate top-level comments
PASS: author operations contain no bare push command
PASS: author operations contain no remote-only push command
PASS: design limits fresh approval to collaboration writes
PASS: design requires explicit remote/refspec
PASS: design preserves push-before-draft
PASS: review frontmatter preserved
PASS: author frontmatter preserved
PASS: review reference exists
PASS: author reference exists
PASS: AGENTS.md unchanged
PASS: implementation plan remains untracked
TOTAL FAILURES: 0
```

The Git warning about inaccessible global excludes was non-fatal and did not
change the VCS result.

## Portability, placeholder, and diff checks

Commands:

```powershell
rg -n -i 'GemInv|dotnet|npm|SalesUseTax|accounting|src/web' '.agents/skills'
rg -n 'T[B]D|T[O]DO|F[I]XME|X[X]X|f[i]ll in|s[i]milar to Task|R[E]PLACE[_ -]?ME' '.agents/skills' 'docs/specs/2026-08-28-github-pr-review-skills-design.md'
git diff --check
```

Output:

```text
skill portability rg exit=1 (1 means no matches)
placeholder rg exit=1 (1 means no matches)
git diff --check exit=0
```

An intentionally overbroad preliminary portability scan also included the
design spec and found only its four explicit GemInv provenance/prohibition
statements (lines 5, 81, 92, and 104). The acceptance scan is correctly scoped
to the portable skill deliverables and has no matches.

## Provider-command check

Command (with an isolated temporary GitHub CLI config path so help did not read
the user's signed-in configuration):

```powershell
$env:GH_CONFIG_DIR = 'F:/Sources/Git/Workbench-pr-review-skills/.gh-help-config'
gh api --help | Select-String -Pattern '--paginate|--slurp|--jq' -Context 0,1
```

Relevant output:

```text
In `--paginate` mode, all pages of results will sequentially be requested...
Pass `--slurp` to wrap all pages of JSON arrays or objects into an outer JSON array.
-q, --jq string        Query to select values from the response using jq syntax
    --paginate         Make additional HTTP requests to fetch all pages of results
    --slurp            Use with "--paginate" to return an array of all pages...
```

This verifies that the documented REST collection flags are supported by the
installed GitHub CLI.

## Bundled validator limitation

Commands:

```powershell
& 'C:/Users/mcuni/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' `
  'C:/Users/mcuni/.codex/skills/.system/skill-creator/scripts/quick_validate.py' `
  '.agents/skills/review-pr'
& 'C:/Users/mcuni/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' `
  'C:/Users/mcuni/.codex/skills/.system/skill-creator/scripts/quick_validate.py' `
  '.agents/skills/handle-pr-feedback'
```

Output for both skills:

```text
ModuleNotFoundError: No module named 'yaml'
quick_validate review-pr exit=1
quick_validate handle-pr-feedback exit=1
```

The validator source was also inspected:

```powershell
rg -n "allowed_properties|argument-hint" `
  'C:/Users/mcuni/.codex/skills/.system/skill-creator/scripts/quick_validate.py'
```

Relevant output:

```text
40:    allowed_properties = {"name", "description", "license", "allowed-tools", "metadata"}
42:    unexpected_keys = set(frontmatter.keys()) - allowed_properties
```

Thus the bundled validator is unavailable because PyYAML is absent and would
also reject the binding `argument-hint` field. Direct assertions verify both
required frontmatter blocks and reference paths instead; no dependency or
validator source was changed.

## Final scope and concerns

- Intended tracked changes: both skill entrypoints, both GitHub operations
  references, the binding design spec, and this report.
- `AGENTS.md` is unchanged.
- `docs/plans/2026-08-28-github-pr-review-skills.md` remains pre-existing and
  untracked as required by repository guidance.
- No live GitHub write, push, issue, comment, review, or thread resolution was
  performed during validation.
- Only concern: the bundled skill validator limitation documented above. The
  direct structural, portability, placeholder, provider-help, and diff checks
  cover the changed behavior and file shape.
