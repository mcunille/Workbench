# Review design proposals and architecture

Use this guidance for a proposal PR or an explicitly requested architectural review. Review the
design as a set of choices, not only as a consistent description. Scale the assessment to the change;
do not require a full system redesign or runtime evidence for behavior that has not been implemented.

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

## Review migration delivery

Whenever a PR adds or changes database migrations, inventory those introduced since its base;
exclude existing base migrations from consolidation. Apply the repository's migration policy.
For Workbench, require one migration per coherent release change. Flag a development sequence
of an initial migration plus corrective migrations and request consolidation before merge,
preserving dependency ordering, custom SQL, security controls, data transformations, rollback
guards and the final model snapshot. Multiple independent release changes are assessed separately.

Verify any claimed need for separate migrations against a concrete staged deployment, backfill or
compatibility boundary. A PR explanation or local preview application is not by itself evidence of
such a release requirement. If the author claims a migration was applied to a retained/shared
environment, explicitly surface the conflict between consolidation and immutable applied history;
do not silently accept the exception or recommend rewriting applied history in place. Identify the
affected environment and evidence available, and request an owner decision on data-preserving
reconciliation or a documented exception. Never infer authorization to delete retained data.
Distinguish an unresolved exception from a proven policy violation; an explicit owner decision to
require consolidation supplies the disposition and must be recorded in the finding.

Have Quality check fresh-database creation and upgrade from the PR base, including retained data,
and inspect updates to migration-history assertions and schema-version references. Report each
migration's purpose, the consolidation decision, verified exception evidence or unresolved owner
decision, and verification limits in the Architecture report. A single coherent migration, a
justified deployment boundary, and a claimed retained-preview exception should lead to different
assessments; the count alone does not establish a defect.

## Report the architectural judgment

Explain what fits the existing architecture, which complexity is justified, and which choices lack
a demonstrated need. Tie recommendations to concrete costs or lifecycle obligations. Distinguish
observed evidence from estimates; do not invent workload or capacity measurements.

Label a correctness defect or substantiated approach-level objection as a required finding. Label
an optional simplification, unresolved future choice, or request for rationale as nonblocking unless
evidence shows it prevents the current requirements from being met. Do not invent a blocker to make
an architectural review appear useful, or silently promote advice when asked to publish it.

State the reviewed scope and limits: design consistency, architectural suitability, static code
inspection, mock interaction, and actual runtime verification are different evidence. Approval of
a proposal is not proof of usability, concurrency, persistence, or capacity.
