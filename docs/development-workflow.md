# Development workflow

Use the relevant section for the work at hand. The root [AGENTS.md](../AGENTS.md) owns
approval boundaries, TDD, mutation testing, and completion requirements;
[CONTRIBUTING.md](../CONTRIBUTING.md) owns application verification gates.

## Design decisions

For changes requiring a spec, capture the problem, users or callers, scope and non-goals,
constraints, credible alternatives and tradeoffs, chosen design, and observable acceptance
criteria. Include affected contracts, failure behavior, security, migrations, and rollback
where relevant. Use existing code and requirements as evidence; avoid speculative abstractions.

Resolve material design decisions before implementation under the root approval policy.
Review the spec for contradictions, missing requirements, and unclear outcomes. Record an
approved design in `docs/specs/`; update living architecture and product documentation when
implementation makes it current. A small correction needs neither a spec nor a design ceremony.

## Planning and recovery

For work needing a plan, divide it into independently verifiable deliverables. Each task names
its owned files or components, dependencies and shared interfaces, acceptance criteria, and
verification. Include enough detail to execute without inventing requirements; complete code
listings and arbitrary minute-by-minute steps are unnecessary.

Check that the tasks collectively satisfy the spec and agree on shared contracts before starting.
Keep the plan and progress notes in task-specific, untracked scratch storage outside committed
documentation. Record completed deliverables, commit IDs or pending diffs, verification evidence,
decisions, blockers, and the next step. After interruption, reconcile those notes with Git and
the current workspace before resuming; do not repeat completed work from memory alone.

Revise the plan as evidence changes while preserving approved scope. Resolve routine implementation
details and continue independent work when another task is blocked. Escalate decisions that cross
the existing design or authorization boundaries, not every failed test or missing dependency.

## Delegation and integration

Use subagents, when available and permitted, for independent tasks or a useful independent review.
Keep small or tightly coupled work inline. Parallel implementation requires non-overlapping file
ownership and no conflicting shared resources; serialize changes to shared interfaces, databases,
or build outputs. Reuse an existing isolated workspace rather than creating a nested worktree.

Give each worker a focused brief containing:

- Objective, owned files, and explicit exclusions.
- Relevant spec sections, exact constraints, and interfaces with other tasks.
- Acceptance criteria and applicable verification commands.
- Required report: changes, evidence, unresolved concerns, and completion status.

Keep briefs self-contained with targeted references rather than a transcript of the whole session.
The primary agent owns integration and Git publication; workers must coordinate shared Git-index
operations and must not revert other workers' changes. Require workers to return blockers to the
primary agent rather than spawning uncoordinated helpers. Follow the session's model settings
unless the user or applicable instructions specify otherwise.

Inspect returned diffs and actual test evidence. A worker's success summary is not proof. Verify
the integrated result, including interactions across task boundaries. Reuse evidence for unchanged
code when trustworthy; rerun affected checks after integration changes or when evidence is incomplete.

## Debugging

Reproduce the failure where possible, inspect errors and recent changes, and trace the failing data
or control flow. Compare with working examples. State a hypothesis and test it with the smallest
useful experiment before accumulating fixes. Follow the root TDD rule for the resulting behavior fix.

Use targeted diagnostics with secrets redacted; never dump environment variables or credentials.
For asynchronous failures, prefer observable conditions and bounded waits over arbitrary sleeps
or merely increasing timeouts. When attempts stop producing new evidence, revisit assumptions,
improve instrumentation, or seek an independent investigation. A retry count alone is not proof
that the architecture is wrong. Report unresolved causes honestly.

## Internal implementation review

Before delivery, check both compliance with the accepted requirements and code quality: missing
or extra scope, correctness, failure paths, maintainability, and meaningful test assertions. For
substantial or risky changes, use an independent reviewer subagent when available and permitted;
otherwise self-review and disclose that limitation. Small changes can be reviewed inline.

Give the reviewer the requirements and the complete immutable base/head range, including every
commit in the task. Do not assume `HEAD~1` covers a multi-commit implementation. Let the reviewer
inspect supporting code without steering it toward a desired verdict. Independently validate
findings, fix substantiated in-scope defects, and re-review the fix and affected interactions.
Reassess scope if the base or contract changes. Do not declare completion while required defects
remain unresolved; explain any proposed deferral.

Internal review is part of implementation and does not authorize GitHub review publication.
For a user-requested PR review or author feedback round, use the repository's `review-pr` or
`handle-pr-feedback` skill and its exact permission contract. Retain the working branch and
worktree for feedback; creation of a PR does not authorize merge or cleanup of unrelated work.

## Guidance maintenance and audit record

Add guidance for demonstrated recurring needs. Use narrow triggers and move detail behind relevant
links. Check changes against representative scenarios, such as a typo, an architectural decision,
independent tasks, overlapping edits, and a blocked test. Distinguish a static scenario walkthrough
from an actual agent experiment. Avoid adding repeated warnings in response to hypothetical failures.

This guidance was selected from a local audit of Superpowers 6.3.0 on 2026-09-05. Its 14 skill
entrypoints were compared with Workbench's existing rules; no vendor scripts or hooks were imported
or executed. This was a workflow audit, not a security audit of the plugin's executable components.

| Source skills | Disposition |
| --- | --- |
| `brainstorming` | Retain scoped design and alternatives; omit approval ceremonies for every change. |
| `writing-plans`, `executing-plans` | Retain task contracts and progress recovery; omit full-code plans and automatic blocker stops. |
| `subagent-driven-development`, `dispatching-parallel-agents` | Retain focused ownership and integration review; omit fixed retry counts, model mandates, and compulsory delegation. |
| `systematic-debugging` | Retain reproduction and hypothesis testing; omit secret-revealing diagnostics and retry-count architecture gates. |
| `requesting-code-review`, `receiving-code-review` | Retain requirements/quality review and evidence-based feedback; keep repository PR skills authoritative. |
| `using-git-worktrees`, `finishing-a-development-branch` | Retain workspace awareness and preservation; keep existing PR delivery and merge boundaries. |
| `test-driven-development`, `verification-before-completion` | Already represented in root guidance; do not duplicate their process. |
| `writing-skills` | Retain narrow scope, selective loading, and scenario checks; omit mandatory pressure-test ceremonies for all documentation. |
| `using-superpowers` | Omit universal activation and recursive skill-loading requirements. |
