---
name: start-refactor
description: Reduce complexity in one existing method, component, or file while preserving observable behavior through characterization tests, mutation evidence, and target coverage. Use for bounded extraction, flattening, deduplication, or file splitting; not features, bug fixes, performance work, or cross-layer redesign.
---

# Start refactor

Preserve the behavior of one named unit while making it easier to understand. Establish the
behavior and its test coverage before restructuring production code. Passing tests provide
bounded evidence, not a mathematical proof of equivalence.

## Scope and authority

Follow the user's explicit scope and existing authorization. Root [AGENTS.md](../../../AGENTS.md)
owns approval and delivery boundaries; [CONTRIBUTING.md](../../../CONTRIBUTING.md) owns application
verification. Resolve routine details and continue through implementation and PR delivery without
asking again for permission already given. Read the relevant sections of
[the development workflow](../../../docs/development-workflow.md) for planning and internal review.

Keep the refactor within one project or feature folder. Allowed moves include method/function
extraction, guard clauses, flattening, local or private/internal renames, constants, deduplication,
and splitting a file into siblings. Preserve public signatures, DTOs, serialization, API and
schema contracts, query semantics, transaction boundaries, side-effect order, errors, and status
codes. New dependencies, abstractions, DI registrations, cross-layer moves, behavior fixes, and
performance changes require a separately scoped task and any applicable design approval.

If a broader change would help, present the evidence and affected locations. File an issue only
when authorized under repository guidance. Continue the bounded refactor if it remains useful.
If a skill rule blocks requested work, link this file, quote the specific rule, and explain the
concrete decision needed; do not turn a suggestion into an approval gate.

## 1. Establish the target and baseline

Inspect Git status, branch, and base commit. Preserve unrelated edits and reuse the current
workspace; isolation is useful but a detached HEAD or unrelated dirty file is not itself a blocker.
Avoid overlapping edits. Create a descriptive `codex/` branch before committing if detached or on
the default branch. Keep an existing task branch unless renaming is clearly useful and it has not
been published; never rename a published branch merely to satisfy this skill.

Use the requested file or symbol. Without a target, inspect a bounded area, rank a few candidates
with evidence (length, nesting, branches, duplication), recommend one, and ask the user to select
unless they already authorized you to choose. Do not scan the entire repository unnecessarily.

Read only the relevant stack reference:

- [dotnet.md](references/dotnet.md) for `src/Workbench.Server` or `src/Workbench.Database`.
- [web.md](references/web.md) for `src/Workbench.Client`.

State the target and make a short untracked plan covering the safety net, structural changes,
verification, and delivery. Use inline execution for tightly coupled moves. Independent inventory
or review work may use subagents when available and permitted; serialize edits and tests sharing
source, build outputs, or databases. Keep current model settings.

Run the affected stack's suite on unchanged code and record the command and outcome. Investigate
baseline failures before changing that target; distinguish unrelated or unavailable checks from
failures of its behavior. Never claim a green baseline from stale binaries or absent output.

## 2. Characterize current behavior

Read the unit and relevant callers. Show an inventory of inputs, branches, outputs, side effects,
error paths, and boundary cases. Include authorization, persistence, concurrency, and asynchronous
ordering when the target participates in them. Map each inventory item to existing or missing tests.

Add missing characterization tests through existing surfaces before restructuring. Use the root
Gherkin-comment requirements. Tests assert what the code currently does and should pass against
unrefactored production code; do not invent a failing new requirement or change production to make
a characterization test pass. Investigate a failure as a mistaken expectation or a latent bug.

Prefer public behavior over exposing implementation. A narrowly justified test-access adjustment
may be kept with the safety-net commit only after checking that it changes no observable behavior;
record it explicitly. Do not extract or reorder code merely to make it testable before coverage
exists. If characterization would require a public-contract or architectural change, present that
specific obstacle under the repository design policy.

For a suspected existing bug, preserve the current behavior and annotate its test with
`NOTE: suspected bug` and the observed discrepancy. Link an existing or authorized issue when one
exists; otherwise report it locally without inventing an issue number or filing automatically.
Do not fix that bug in the refactor.

## 3. Validate and commit the safety net

Use available mutation tooling on affected code to check meaningful changes to the inventory's
behavior. Prioritize business rules, authorization, validation, and state transitions. Investigate
survivors; strengthen weak assertions and document equivalent mutants or justified exclusions.
When no suitable runner is available, use small reversible manual mutations where practical and
state the automated-tooling limitation. Report any untested behavior explicitly.

A useful mutation changes a comparison, default, return value, or side effect and triggers an
expected assertion failure. Compilation errors, setup failures, and timeouts do not show that an
assertion detects the change. Record actual evidence:

| Inventory item / test | Mutation | Observed assertion failure or survivor explanation |
| --- | --- | --- |

Restore each mutation and inspect the diff before continuing. Do not discard unrelated changes or
reset the whole workspace. Run the restored tests green. Commit the safety net separately from
structural edits; if existing tests already cover the inventory, record that evidence without
creating an empty commit.

## 4. Refactor and verify

Apply one coherent structural move at a time. Run focused tests after each move and broaden when
the affected surface or a failure warrants it. Existing characterization tests protect extractions;
do not require a deliberately non-compiling test for every new private helper. Add direct helper
tests when they cover meaningful behavior, not merely the implementation's shape.

If a move fails, inspect the cause and restore or correct that move without changing expected
behavior. Keep any necessary new characterization coverage anchored to the pre-refactor behavior.
Run the affected suite after the final move. Compare the diff against the inventory, including
moved code and every new sibling file, and check for observable behavior changes and scope creep.

Use [coverage-gate.ps1](scripts/coverage-gate.ps1) to measure all refactored production files:

```powershell
pwsh -NoProfile -File .agents/skills/start-refactor/scripts/coverage-gate.ps1 -Stack api -Target SessionService.cs,SessionValidation.cs
```

Use `-Stack web` for the client and sufficiently specific source path fragments. Pass multiple
targets comma-separated under `pwsh -File`. Each matched file must reach **95% line coverage and
95% branch coverage independently**. Missing targets, empty measurements, test failures, and
missing reports fail the gate. A file with no branches reports that fact instead of inventing
branch coverage. Include every new sibling so splitting does not hide unmeasured code.

Add meaningful characterization tests for uncovered paths and assess their mutation sensitivity.
Never lower the threshold or expand measurement to tests to manufacture a pass. If coverage cannot
reach the threshold within scope, report actual numbers and the reason; user acceptance of the
specific shortfall is required before treating the refactor as complete. Missing measurement tools
are a stated verification limitation, not a passing gate. Continue independent work while resolving
that limitation. `-SkipRun -ReportPath <xml>` only re-evaluates a report whose source and test
freshness you have verified; it does not prove freshness itself.

Complete the application gates in CONTRIBUTING.md. Inspect affected running workflows when required
by the relevant repository guidance and provide local URLs. Report unavailable checks accurately.
Do not repeat successful checks without a new change, failure, or unresolved concern.

## 5. Review and deliver

Review the complete task diff against the behavior inventory; map every item to test evidence and
resolve in-scope findings. Follow the development workflow's internal review rules; a separate PR
review or security scan is not automatically required for every refactor. Keep pre-existing bugs
separate from regressions introduced by the refactor.

Commit structural changes separately, push the task branch, and open or update a ready-for-review
PR under root guidance. Include the target, measured complexity before/after, moves, behavior/test
mapping, mutation evidence and limitations, coverage numbers, and verification results. Keep the
summary proportional to the change. Leave merging and separately gated collaboration writes to
their existing authorization rules.

## Migration basis

Adapted from GemInv's `start-refactor` on 2026-09-05. The
[official GPT-6 Astra guidance](https://developers.openai.com/api/docs/guides/latest-model?model=gpt-6-astra)
was checked that day. This version retains characterization, mutation evidence, separate commits,
and target coverage, while clarifying authorization, limiting repeated testing, and removing
GemInv-specific paths and mandatory compile-failure ceremonies. No model configuration is changed.
