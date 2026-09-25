---
name: doc-audit
description: "Use when auditing repository documentation for obsolete, redundant, or superseded material, retiring implemented specs, or distilling decisions and lessons into durable docs."
---

# Documentation Audit

Keep the smallest useful documentation set. Code and tests own implementation mechanics;
durable docs explain intent, constraints, tradeoffs, and how people use or operate the system.
Optimize for retained knowledge and clear ownership, not deletion counts.

## Scope and authority

An audit or review request produces findings without editing repository files. A request to
apply cleanup authorizes the selected documentation edits; preserve existing authorization
and follow root/scoped AGENTS.md and the [development workflow](../../../docs/development-workflow.md).
Do not expand a documentation cleanup into code fixes, policy changes, issue publication,
merging, or production operations. Ordinary documentation edits do not trigger a wider audit.

Start with the requested scope and [documentation index](../../../docs/README.md). Name what
was inspected and what remains uninspected; a sampled audit is not a repository-wide verdict.
Keep working inventories and evidence in the workflow's ignored workspace, not another
permanent audit document.

## Review by claim, not age

Inventory the scoped docs and their audience, purpose, current owner, and incoming links.
Read candidate sections, then inspect relevant code, tests, configuration, scripts, and
selective Git history. Status labels and old timestamps are leads, not proof of obsolescence.
A passing test or implemented feature does not establish deployment or operational readiness.

Separate facts about current behavior from intended contracts. When an accepted spec and
code disagree, record the conflict and missing evidence as **investigate**. Do not rewrite
intent to match code or declare the feature complete from a status label. Keep unfinished
requirements and unresolved decisions visible.

For generated docs, compare the output with its authoritative source and generation process.
If drift is merely suspected, investigate first; regeneration in an isolated comparison can
supply evidence, but does not by itself justify changing the source, generator, or published
output. Propose the appropriate owner to fix after locating the drift; avoid hand edits.

| Action | Evidence needed |
| --- | --- |
| Keep | Distinct current audience, procedure, contract, decision, or required historical record. |
| Update | Confirmed stale claim and evidence for its replacement. |
| Consolidate | Overlap with a named canonical owner; preserve distinct audience needs. |
| Distill then delete | Redundant source with unique knowledge mapped to a durable destination. |
| Delete | Superseded or unnecessary content, no remaining unique value or retention requirement, and references accounted for. |
| Investigate | Uncertain completion, conflicting sources, unknown retention, or insufficient evidence. |

## Distill before retiring

For each candidate spec, lesson, or work log, extract only knowledge that still changes a
future decision: why an approach was chosen, rejected alternatives and their reasons,
business/security invariants, non-obvious infrastructure limits, compatibility obligations,
recovery procedures, and unresolved work. Verify that each lesson still applies; a single
incident or workaround is not automatically a universal rule.

Choose an existing maintained owner: architecture for system decisions, product guidance for
intent, a runbook for operations, or contributor guidance for development constraints. Write
a concise current statement with its rationale and applicability. Keep provenance when it
helps explain the decision; clearly label historical measurements and verification dates.
Create a new decision record only when no existing owner fits and its value warrants one.

Remove task checklists, copied code descriptions, and implementation chronology once their
useful content is preserved. A completed spec with nothing unique left can be deleted. An
abandoned proposal can still contain a current constraint worth extracting. Archive only
when a specific historical, contractual, or audit need requires the original record; moving
all stale docs into an archive merely relocates maintenance burden.

Example: an import spec repeats parser mappings but uniquely explains that notifications
are suppressed because the supplier charges per email. Preserve that constraint and reason
in the maintained import guide, link to code for mappings, then retire the redundant spec.

## Deliver an actionable audit

Lead with scope, key findings, and evidence limits. Use a concise table:

| Document / section | Action | Evidence / uncertainty | Knowledge to preserve and destination | References / verification |
| --- | --- | --- | --- | --- |

Use actual file/section or symbol references. Name a concrete destination for every extracted
fact; distinguish proposed text from verified current facts. Include a short preservation
excerpt when a deletion depends on wording. Group unchanged keep decisions when that reduces
noise. Finish with an ordered cleanup batch and any decisions requiring user input.

## Apply and verify authorized cleanup

1. Distill into the maintained owners, then remove redundant material. Preserve unrelated edits.
2. Repair indexes, relative links, anchors, script references, and documentation assertions.
   Check known external consumers too: retain a minimal forwarding page or coordinate their
   migration when deleting a public path would break them. Report what could not be checked.
3. Re-read the diff for lost rationale, altered intent, unsupported claims, contradictory
   owners, and historical evidence presented as current. Check the preservation mapping.
4. Run relevant documentation checks and link/anchor checks plus `git diff --check`. Inspect
   path/status assertions before changing their expected values; do not weaken a contract
   merely to make a documentation deletion pass. Report unavailable checks explicitly.
5. Report what was consolidated, preserved, removed, and left unresolved. Follow repository
   delivery rules for authorized edits. Do not claim an application build, runtime check,
   or mutation run from documentation-only validation.
