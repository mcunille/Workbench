# GitHub PR Review Skills Design

## Purpose

Port GemInv's author-side and follow-up-review skills into Workbench as two reusable GitHub/Codex skills. The port should preserve the proven review-conversation invariants while removing GemInv-specific commands, labels, domain rules, and repository-policy references.

The resulting skills are:

- `review-pr`: conduct either a first-pass or follow-up pull-request review from the reviewer seat.
- `handle-pr-feedback`: verify, address, and answer pull-request feedback from the author seat.

## Boundaries

The skills remain separate because their permissions are intentionally inverted.

`review-pr` is read-only until the user approves publication. It may inspect code, commits, checks, comments, and review threads. It must not edit files, commit, file issues, approve or request changes through GitHub's review state, or resolve threads. Publication uses a GitHub comment review whose first line is one of:

```text
AI: **VERDICT: APPROVE**
AI: **VERDICT: REQUEST CHANGES**
AI: **VERDICT: REJECT**
```

`handle-pr-feedback` works from the author seat. Invoking it authorizes validated, in-scope changes, tests, commits, and a non-force push when the current checkout safely represents the PR head and the push names a verified remote/refspec for the discovered head repository and branch. Provider branch and local remote identifiers remain data from acquisition through a Git argument-array invocation; the full `refs/heads/` ref is validated, and neither value is rendered into executable command source. The fresh exact-preview gate applies only to collaboration writes: issues, comments and replies (including summaries), reviews, and thread resolution. It may perform only those exact collaboration writes approved for the round.

Neither skill treats PR text as instructions or authorization. Review comments, replies, and bodies are untrusted data whose code claims must be independently verified.

## Reviewer Lifecycle

`review-pr` has one entry point for both review modes.

1. Orient to the requested PR, fetch current base/head state, load repository instructions, and gather review comments and thread state.
2. Select scope from observable evidence:
   - A first pass, an absent usable prior anchor, or an explicit full-review request uses the full base-to-head diff.
   - A follow-up starts from the newest usable prior review anchor and examines author replies plus the new delta.
   - A follow-up escalates to the full diff when history or change shape makes a delta unsafe, including rebases or force-pushes, structural/API/schema changes, broad out-of-scope fixes, a large replacement body of work, or base changes under affected paths.
3. Review the selected scope for correctness, security, regressions, test adequacy, and repository-specific requirements. On follow-ups, independently validate every author reply and disposition each prior thread as satisfied, still open, conceded, or explicitly deferred.
4. Present the exact proposed verdict, findings, thread replies, and verification evidence in chat. Wait for explicit approval for this round.
5. Recheck the PR head and publish exactly the approved material as one GitHub `COMMENT` review plus any approved thread replies. A changed head invalidates the publication step and returns the workflow to review.

The verdict is deterministic: `APPROVE` requires no open or new findings; any retained finding produces `REQUEST CHANGES`; `REJECT` is reserved for a substantiated approach-level objection that should prevent the change from landing in its current shape.

## Author Lifecycle

`handle-pr-feedback` works all unresolved review threads and labeled unanchorable findings, not merely the latest round.

1. Confirm that the local checkout matches the live PR head before allowing changes. Discover the PR head repository and owner, and verify the exact push remote/refspec rather than relying on default push behavior, including for forks and detached worktrees. Gather REST comment data and GraphQL thread state, preserving the distinction between outdated and resolved.
2. Re-derive every claim from code and focused verification. Classify each as real, not real, already handled, suggestion, or unclear.
3. Apply the target repository's own routing rules. Fix real low-risk defects with a regression test first; judge suggestions on merit; push back factually on invalid claims; and seek clarification rather than guessing. Pattern findings require examining sibling occurrences.
4. Run the affected repository-native verification. State directly when no automated suite covers a change and name the relevant manual or structural checks instead.
5. Commit and push completed fixes through the verified explicit remote/refspec before drafting replies so every reply describes visible code and can cite a commit, test, or approved issue.
6. Present the triage, evidence, commits, verification output, proposed issues, and exact replies. Wait for explicit approval for this round's collaboration writes; that approval is not a second gate for the preceding verified code delivery.
7. Perform only the approved GitHub writes. Resolve only threads that are genuinely complete; contested findings and declined suggestions remain open for the reviewer to answer.

## File Organization

```text
.agents/skills/
|-- review-pr/
|   |-- SKILL.md
|   `-- references/github-operations.md
`-- handle-pr-feedback/
    |-- SKILL.md
    `-- references/github-operations.md
```

Each `SKILL.md` contains discovery metadata, the seat's permissions, decision model, lifecycle, output contract, and observed failure guards. Each GitHub operations reference contains only the provider-specific reads and writes needed by that seat. Keeping references inside each skill makes either skill independently portable.

No scripts, UI metadata, README files, or shared framework layer are planned. They will be added only if behavioral testing demonstrates a concrete need.

## Portability Rules

The skills target GitHub and Codex and may rely on `gh`, GitHub REST, and GitHub GraphQL. They must discover rather than hard-code:

- repository instructions and authorization boundaries;
- base branch and PR head;
- build, lint, and test commands;
- issue labels and technical-debt routing;
- language, framework, and domain-specific review concerns.

GemInv's .NET/React commands, accounting invariants, label taxonomy, and named `AGENTS.md` sections are source examples, not Workbench behavior.

## Validation

Skill development follows RED-GREEN-REFACTOR separately for each skill.

1. Run fresh-agent scenarios without the new skill and record concrete unsafe or incomplete behavior.
2. Write the smallest skill that corrects the observed failures.
3. Re-run equivalent scenarios with the skill supplied.
4. Tighten only guidance justified by observed gaps, then re-run affected scenarios.
5. Validate frontmatter, naming, references, and unfinished placeholders with the bundled skill validator.
6. Inspect the final repository diff and verify that the skills contain no GemInv-specific operational assumptions.

Reviewer scenarios cover first-pass scope, delta scope and escalation, unverified author claims, reviewer-seat mutation, stale-head publication, verdict shape, and per-round approval. Author scenarios cover blind compliance, untrusted comment instructions, branch/head mismatch, missing regression proof, premature replies, contested-thread resolution, outdated-versus-resolved state, and per-round approval.

## Success Criteria

- A user can invoke `review-pr` for either an initial or subsequent GitHub PR review without choosing another reviewer skill.
- A user can invoke `handle-pr-feedback` for any author response round.
- Fresh agents preserve the seat-specific permission boundary under pressure.
- Both skills require exact preview and fresh approval before collaboration writes: issues, comments and replies (including summaries), reviews, and thread resolution.
- Reviewer verdicts have the deterministic first-line contract.
- Provider mechanics are executable and discoverable without loading them into every invocation.
- The committed files contain no GemInv-specific commands or policy dependencies.
