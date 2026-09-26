# Review design proposals and architecture

Use the relevant sections during every Architecture council pass, including implementation PRs.
Review design choices and the cost of existing conventions the change exercises, not only whether
the implementation follows them. Scale the assessment to the change; do not require a full system
redesign or runtime evidence for behavior that has not been implemented.

## Trace requirements to mechanisms

Identify the user outcome and invariants, then examine module ownership, transaction boundaries,
data lifecycle, compatibility, and operational cost against the repository's architecture. For a
costly or durable mechanism, ask which requirement needs it and whether a simpler alternative
preserves that requirement. An explicitly acknowledged tradeoff still deserves this assessment.
Do not assume a mechanism is necessary just because the proposal describes it thoroughly.

Separate guarantees that happen to share storage or code. For example, preventing duplicate retries,
reproducing an original response, recovering current work, and retaining business audit history are
different requirements. A stable random request ID retained across retries, with a durable receipt
recorded atomically with the operation, can prevent duplicate execution without full historical
snapshots. A content fingerprint can detect reuse with different input. This is an alternative to
evaluate, not a universal prescription: exact response replay or required business history may
justify snapshots. Changing that guarantee requires an explicit contract change.

Follow retained data through lifecycle transitions and future schema changes. Distinguish temporary
editing/recovery state from committed business records and from retry evidence. Do not recommend
pruning evidence if delayed requests could then execute again. State which identities, receipts,
historical representations, or compatibility rules a simpler design must preserve.

## Assess change locality and repeated work

Start from the complete diff and the requested behavior. Group changed files into feature
implementation, meaningful tests, generated artifacts, migration history, documentation and
mechanical edits. When a bounded feature touches unrelated domains or produces a large diff,
quantify the main contributors and trace why those files must change. Verify claims such as
"only a revision literal changed" against the actual before/after contents, not filenames alone.
File count is a signal to investigate, not a verdict or a fixed threshold.

Look for feature changes that force edits across unrelated modules, repeated contract/configuration
literals, overlapping sources of truth, and mechanisms whose maintenance cost exceeds the current
requirement. Do not excuse demonstrated coupling merely because it predates the PR or follows an
accepted convention. Explain the concrete trigger, affected callers, recurring work or regression
risk, and the smallest coherent correction. Distinguish costs introduced by this PR from existing
costs exposed by it; surface broader remediation separately instead of requiring unrelated cleanup.

For generated artifacts, trace authoritative inputs, generators, consumers and validation. Separate
handwritten duplication from derived output. Assess why each output is committed, its diff churn,
merge/review cost, and whether generation during build/CI preserves the required developer and
release workflows. Large generated files are not automatically defects, but generation does not
make their repository and review costs irrelevant. Recommendations to stop tracking outputs must
preserve reproducibility, clean-checkout builds, client types and contract checks; retain artifacts
when an actual consumer or workflow justifies them.

Record material change-locality and artifact-ownership conclusions in the Architecture report
and final review, including evidence and justified exceptions. Explaining why files changed is
not the same as judging whether the recurring work is proportionate to the feature. Separate
necessary generated output from avoidable handwritten duplication, scattered version markers,
and cross-feature maintenance. Surface demonstrated costs even when they follow existing conventions;
do not classify a large file count alone as technical debt. Merge overlapping symptoms under their
root cause; for example, a global revision bump and dozens of unrelated literal edits are one coupling finding.
Separate substantiated policy/correctness violations from nonblocking simplifications, and record
an explicit owner decision when it changes the required outcome. Assess current user requirements
even when older design documents authorize the convention being reconsidered.

## Review migration delivery

Whenever a PR adds or changes database migrations, inventory those introduced since its base;
exclude existing base migrations from consolidation. Apply the repository's migration policy.
For Workbench, require one migration per coherent release change. Flag a development sequence
of an initial migration plus corrective migrations and request consolidation before merge,
preserving dependency ordering, custom SQL, security controls, data transformations, rollback
guards and the final model snapshot. Multiple independent release changes are assessed separately.

Migrations become durable when their PR merges into main. Verify any claimed need for separate
migrations against a concrete staged deployment, backfill, or release compatibility boundary.
Applying an unmerged migration to a local, retained, or shared preview does not prevent
consolidation and does not create a supported upgrade baseline. Do not request a release fix
solely because an earlier unmerged preview cannot upgrade to the current PR. Preserving preview
data is a separate local operation; never infer authorization to delete data or reset its history.
Already-merged migrations and historical transitions remain immutable.

Have Quality check fresh-database creation and upgrade from the merged PR base, including retained
data, and inspect migration-history assertions and schema-version references. Report each new
migration's purpose, the consolidation decision, any concrete release boundary requiring separate
migrations, and verification limits. Surface the maintenance cost of a justified multi-stage
release without treating preview use as an exception to consolidation. The migration count alone
does not establish a defect.

## Report the architectural judgment

Explain what fits the existing architecture, which complexity is justified, and which choices lack
a demonstrated need. Tie recommendations to concrete costs or lifecycle obligations. Distinguish
observed evidence from estimates; do not invent workload or capacity measurements.

Label a correctness defect or substantiated approach-level objection as a required finding. Label
an optional simplification, unresolved future choice, or request for rationale as nonblocking unless
evidence shows it prevents the current requirements from being met. Do not invent a blocker to make
an architectural review appear useful, or silently promote advice when asked to publish it.

When material debt or tradeoffs exist, include **Architectural debt and tradeoffs** in the final
chat assessment, even with an APPROVE verdict. Include it in a grouped review body only when the user explicitly requests that body. For each item,
state the affected paths and measured scope, the concrete recurring cost or risk, why the current
approach is necessary or avoidable, and a bounded improvement or explicit acceptance decision.
Distinguish debt introduced by the PR from existing debt it exercises. Give actionable findings
a severity and required-fix or nonblocking disposition; label a justified retained tradeoff as
nonblocking without inventing a policy violation. An explanation such as "most lines are generated"
or "the migration was applied locally" must not replace this assessment. If no material concern
remains after inspection, say so briefly rather than manufacturing a recommendation.

State the reviewed scope and limits: design consistency, architectural suitability, static code
inspection, mock interaction, and actual runtime verification are different evidence. Approval of
a proposal is not proof of usability, concurrency, persistence, or capacity.
