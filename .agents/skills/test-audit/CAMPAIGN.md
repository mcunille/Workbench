# Test-pruning campaign

Adapted from [OpenClaw's test-audit skill](https://github.com/openclaw/openclaw/tree/main/.agents/skills/test-audit)
using the user-supplied `SKILL.md` and `CAMPAIGN.md` snapshots. Credit to the
OpenClaw contributors. The upstream MIT copyright and permission notice is
preserved in [LICENSE](LICENSE). Workbench adaptations replace repository-specific
commands and clarify test ownership and authorization boundaries.

Campaign mode audits one Workbench feature's whole test surface across its
server, SQL, client, and browser owners for one coherent PR. The value bar, retention bar,
candidate evidence, and validation in [SKILL.md](SKILL.md) apply to every
lane. This file adds the order of work and the lessons of a full campaign.
Each step ends on its completion criterion; do not start the next step early.
A read-only request stops after discovery and reporting. Cutover, mutation probes,
defect fixes, and delivery require authorized implementation and the repository's
design/planning handoffs. Keep ledgers and lane plans in the ignored workspace
specified by the development workflow; do not create a second planning process.
The Telegram examples below are lessons reported by the upstream OpenClaw campaign,
not results measured in Workbench.

## 1. Baseline

Record the subsystem's test and support line counts and every test file's
pass/fail state at a pinned `main` SHA. Keep baseline failures in their own
list: in the Telegram campaign, all three were real delivery bugs, not stale
tests.

Done when every in-scope test file has a recorded baseline result.

## 2. Lanes and inventory

Split the surface into **lanes** along production owner boundaries, not file
prefixes. For Telegram these were accounts, commands, context, dispatch,
inbound, outbound, persistence, transport, shared, harness, and live/QA
scenarios. Include the subsystem's cases at shared core boundaries and its QA
and live-proof harness tests.

Done when every test file and QA scenario the subsystem owns belongs to exactly
one lane.

## 3. Read-only ledger per lane

Give independent lanes to read-only agents when available; otherwise process
them sequentially and disclose the lack of independent review. The agent reads every assigned test
in full, including parameter tables. It also reads the production owners and
their entry points, callers, history, and CI routing. Each test declaration
goes into a written **ledger** with one mark. A parameterized test (`it.each` or an xUnit theory) is one declaration
unless its rows need different marks; then mark each row.

- `R`: retain, naming the contract and the bug it catches; a retained test that
  only moves to a better-named file stays `R` with the move noted;
- `F`: retain the contract but repair the assertion, such as a vacuous negative
  that passes when only one of several items is missing;
- `C`: consolidate, naming the owner that absorbs the assertion first: a sibling
  table case, a stronger boundary suite, or the shared owner in another package;
- `D`: delete, naming the proof that remains, or why no contract exists.

Judge a test by its assertions, not its name. One Telegram test named for
retiring a progress window asserted the window was _not_ cleared.

Done when every declaration in the lane has a mark and an evidence line.

## 4. Layer plan per lane

Treat the per-test ledger as input, not as the edit list. A second read-only
pass, starting from the ledger, looks for the redundant **layer**. In Telegram,
several dispatch suites replayed the same shared compositor through one mocked
preview, around stronger real-stream and HTTP-fixture suites. Name the
**keeper** suite for each contract. Prefer the real transport boundary with a
fake network over a mocked collaborator for transport contracts. Choose one primary
owner per contract; keep another layer only for a concrete failure that owner cannot
catch. Database enforcement needs real SQL evidence, which may already be exercised
through an HTTP owner; do not add a second suite merely to cover another layer. Correct any ledger errors this pass
finds.

Done when each lane plan names its retired files, its keeper per contract, the
assertions to carry into keepers, and the test-only production seams unlocked.

## 5. Cutover

Edit lane by lane. Serialize changes to shared harnesses and support files
through one owner. With each lane, remove the test-only production seams it
unlocks: injection parameters, getters, reset exports, and indirection layers.
Register moved suites in applicable CI routing and test inventories. Update
existing size baselines only when the repository uses them. Put durable test-ownership rules in the subsystem's
`AGENTS.md`, drawn from mistakes this campaign actually found.

Done when every lane plan is applied and each lane's keepers pass.

## 6. Preservation review

Before claiming completion, have independent reviewers compare deleted
coverage against the keepers, one reviewer per boundary group. They look for
contracts that lost their only proof. They also look for new assertions that
cannot fail, such as a rejection row the production code never reaches. The
Telegram review found nine real gaps and one unreachable assertion.

For each restored contract, make one deliberate **mutation** of the production
owner and confirm the keeper goes red. Then restore the pre-probe source byte for
byte, preserving pre-existing edits. Do not mutate code during a read-only audit.

Done when every reported gap is restored or rejected with source evidence, and
every restored contract has a caught mutation.

## 7. Product defects

A baseline failure that survives into a keeper is a possible product bug, not a
deletion candidate. Reproduce and report it. When its repair is within approved
scope, fix it at its owner as a separate commit and prove the real user flow with a
**control** run that reverts the fix and shows the old behavior. Record
unrelated product discrepancies you find as follow-ups instead of fixing them
in the campaign.

Done when each repaired defect has a failing control and a passing candidate
on the same harness, and out-of-scope defects are reported as unresolved follow-ups.
Report follow-ups in the handoff; filing issues requires its own authorization.

## 8. Reconcile and hand off

Campaigns may outlive many `main` commits. Reconcile with the current base using
the repository workflow and obtain explicit authorization for merges. When the
base modifies a file proposed for deletion, reassess the deletion and preserve
every new contract in a keeper before removing the old file. Rerun the whole
subsystem suite and repeat applicable live proof on the reconciled head.

Expect review tooling to see a truncated file list on a diff this large.
Record maintainer decisions for generic compatibility flags in the PR evidence
rather than editing gates.

Hand off with the [SKILL.md](SKILL.md) report, plus:

- baseline and final test/support line counts, with production counted separately;
- lanes, retired layers, and keepers;
- preservation gaps found and their mutations;
- product defects with control and candidate proof.
