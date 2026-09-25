---
name: test-audit
description: "Use when writing, changing, reviewing, or auditing tests for low-value, implementation-coupled, or duplicative coverage and test-only production seams."
---

# Test Audit

Adapted from [OpenClaw's test-audit skill](https://github.com/openclaw/openclaw/tree/main/.agents/skills/test-audit)
using the user-supplied `SKILL.md` and `CAMPAIGN.md` snapshots. Credit to the
OpenClaw contributors. The upstream MIT copyright and permission notice is
preserved in [LICENSE](LICENSE). Workbench adaptations replace repository-specific
commands and clarify test ownership and authorization boundaries.

Three modes, one value bar. Authoring mode gates every new or changed test at
write time. Audit mode runs focused sweeps of tests that re-assert source,
duplicate stronger proof, couple behavior to implementation, or keep test-only
production seams alive. Continue broad audits as separate coherent follow-up
PRs; optimize for confidence, not deletion count. Campaign mode prunes one
whole subsystem's test surface (every test file the selected feature owns);
before starting one, read [CAMPAIGN.md](CAMPAIGN.md).

## Workbench scope and workflow

Use the authoring gate within the current task; it does not start a broader audit.
Audit and campaign discovery stay read-only until edits are authorized. A request
for review or findings does not authorize deletions, defect fixes, mutations,
commits, or publication. Follow root and scoped `AGENTS.md` and the design and
planning handoffs in [the development workflow](../../../docs/development-workflow.md)
before implementation. Keep working ledgers in its ignored execution workspace.

Choose one primary owner per contract: the boundary that catches the credible
regression with the strongest evidence and lowest maintenance/runtime cost.
[Tests/README.md](../../../tests/README.md) helps select that boundary; its layers
are not a checklist requiring the same assertion at HTTP, SQL, and browser levels.
Another layer earns coverage only by naming a distinct failure the primary owner
cannot catch. Otherwise consolidate into the best owner and remove the duplicate.

For example, HTTP denial and direct SQL tenant-bypass tests may protect different
guards, but retain both only with evidence of those independent failure modes.
Use real SQL when proving database enforcement; an HTTP test that exercises the
real database may already own the contract. Browser coverage must likewise add a
browser-specific failure or necessary integration proof, rather than replay the
same lower-layer matrix. Keep the smallest cases needed for that distinct risk.

Keep repository TDD, characterization, Gherkin comments, and focused mutation
requirements. Use `start-refactor` for applicable behavior-preserving refactors;
use `review-pr` only for user-requested PR review with its publication gate.

## Authoring gate

Before adding any test, answer four questions; a missing answer means do not
add it yet:

1. What observable behavior, invariant, or independent contract does it protect?
2. What credible regression makes it fail?
3. Why does existing coverage not already catch that failure? Each contract has
   one primary test owner at the strongest boundary; another layer needs its
   own distinct risk, such as a transport or lifecycle failure the owner cannot
   reach. Prefer extending a table-driven case or shared fixture over a
   near-duplicate test; consolidate duplicated setup in the same change.
4. Does it need a production seam (export, flag, wrapper, injection hook) that no
   production caller needs? If yes, move the test to the real boundary instead.

Then check the test against every [junk pattern](#junk-patterns); a match fails
the gate unless the [retention bar](#retention-bar) names the contract it
independently guards. A test that would break under behavior-preserving
refactoring is asserting implementation, not behavior; rewrite it at the
owning boundary before landing it.

Bug regression tests must fail on the pre-fix code for the intended reason and
pass after the owner-boundary repair. A regression test that never demonstrably
failed proves the mock, not the fix. One regression at the owner boundary
covers the bug; do not replay the same scenario at every layer it crosses.

## Junk patterns

The shared checklist for both modes: the authoring gate rejects a new test that
matches one, and audits hunt for existing tests that do.

- assertion-free coverage probes;
- self-comparisons and identity copiers;
- copied fixtures, inventories, manifests, or export lists;
- exact source, import, or string greps;
- private predicate or call-shape tests duplicated at real boundaries;
- duplicate invocations of the same contract;
- provider-local replays of shared helpers;
- tests whose only purpose is preserving test-only exports, globals, or wrappers;
- dead production code whose only callers are tests;
- expected values produced by the helper or renderer under test;
- mocks that implement the asserted behavior, or one identical mock standing in
  for different APIs;
- fixtures that supply the receipt, admission, or callback ordering the owner
  should produce, or persistence asserted against a store the path never writes;
- capability tests that restate declared flags instead of exercising the
  delivery or acknowledgement the flag promises;
- negative controls that pass for an unrelated reason, such as a denial from a
  different guard or a rejection the production path never reaches;
- names or fixtures that promise more than the input exercises, such as a
  "retires the window" test asserting the window was not cleared.

## Value bar

Tests justify their maintenance cost by protecting behavior, a credible
regression, or an independently meaningful contract. In an audit, an existing
test that must change for behavior-preserving source reorganization is suspect,
not automatically deletable; the authoring gate still rejects new ones.

Before judging a candidate, read the complete test and production owner, its
entry point, callers, callees, sibling implementations, overlapping tests, CI
routing, and relevant history. Read root and scoped `AGENTS.md` files first.
When the test claims dependency-backed behavior, inspect the dependency source
or types directly.

## Discovery

Keep discovery read-only and report evidence before editing. For broad scope,
run parallel discovery lanes when available:

- server and API tests (`src/Workbench.Server/`, `tests/Workbench.Server.IntegrationTests/`);
- client components and transport tests (`src/Workbench.Client/`);
- browser workflows (`tests/Workbench.BrowserTests/`), scripts, and tooling;
- a cross-cutting pattern sweep.

Outside campaign mode, prefer a few high-confidence candidates over a large
speculative inventory. Hunt for the [junk patterns](#junk-patterns).

## Retention bar

Keep a test when it independently enforces a public API, plugin SDK, protocol,
config, migration, storage, security, platform, default, prompt-byte, generated
cross-language, package, release, or architecture contract. Also keep:

- call ordering when order is observable behavior;
- regressions with a credible failure mode;
- source inspection when it is the cheapest independent guard: it fails when
  the contract changes (the user-facing key, byte, or path) and survives an
  identifier-only refactor;
- a retained test that fails on the baseline: treat it as a possible product
  bug; report it, and repair the owner only within authorized implementation scope.

Static or slow is not a deletion reason. A test that resembles implementation
may still be the independent contract; prove otherwise before removing it.

## Candidate evidence

Record every field below before editing. A missing field means the candidate is
not ready for deletion:

- exact test name and location;
- what failure it can actually detect;
- non-test callers of the covered production or support seam;
- stronger remaining owner-boundary proof, or why no proof is needed;
- relevant history and the reason the test or seam exists;
- production or test-support deletion unlocked;
- risk and the focused validation command.

## Edit shape

Choose one coherent owner-boundary batch. Delete obsolete test-only exports,
globals, wrappers, and dead production paths instead of preserving aliases.
Move retained regressions to their canonical owners. Consolidate repeated
package or dependency assertions into one generic contract.

Prefer net-negative production LOC. Do not add replacement tests that restate
the same implementation, and do not convert uncertain candidates into cleanup
to increase deletion counts.

## Validation

Serialize edits and test runs that share checkout files, databases, or build
outputs. Follow [CONTRIBUTING.md](../../../CONTRIBUTING.md) for authoritative gates.

1. Run owner and sibling tests from current source with
   `./scripts/test-focused.ps1 -ServerFilter '<actual filter>'`,
   `-ClientFiles '<actual test filename>'`, or `-BrowserFiles '<actual spec filename>'`.
   Use real selectors from the checkout and install dependencies as documented.
2. For removed source greps or plan assertions, run the executable script or
   dry-run that owns the real contract. Map each removed/moved case to remaining
   coverage and report intentional discovery-count changes.
3. Run targeted formatting and `git diff --check`. Run meaningful focused
   mutation probes; report unavailable tooling and surviving mutants honestly.
4. Run the applicable delivery gates, including `./scripts/verify.ps1` and
   `./scripts/smoke-container.ps1` for application/test changes. Focused runs do
   not replace them. Documentation-only changes use affected guidance checks.
   Refresh and inspect the isolated preview when the affected workflow requires it.
5. Inspect `git diff --numstat`; report production/tooling separately from tests
   and test support. If claiming a performance improvement, measure comparable
   before/after cohorts as described in the test-ownership guide.
6. Complete internal review through the selected superpowers workflow; review
   the entire change. State verification blockers without claiming completion.

## Landing and continuation

After authorized implementation is complete and verified, commit, push, and open
or update a ready-for-review PR under the repository's delivery rules. Keep the
branch and worktree for feedback. Merging, production operations, and separately
gated collaboration writes require their own explicit authorization. After a
merge, refresh from current `main` and repeat read-only discovery only within the
requested scope; do not start another cleanup batch automatically.

## Handoff

Report:

- root cause and removed low-value categories;
- production owner simplifications;
- retained false positives and why they remain valuable;
- focused and full proof actually run;
- production versus test LOC;
- PR and merge state;
- named follow-ups.
