# Test value and cost

Quality reads this on every review pass. Apply it to tests added, changed, removed, or relied on
by the diff, consulting nearby coverage to establish ownership. Do not turn a bounded PR review
into a whole-suite audit or require new tests for changes that do not affect behavior.

## Require a distinct regression claim

For each added or materially changed test, identify the plausible defect it detects, the observable
outcome that would fail, and why existing coverage is insufficient. Parameterized rows can be
assessed as a group, but each input class must exercise a distinct rule, boundary, or failure mode.
Test count and coverage percentage are not evidence of value; neither is a different test name.

Read setup, doubles, actions, and assertions. Look for assertions true before the action, aliases
compared to themselves, failure cases whose baseline already fails, broad exceptions that accept
the wrong failure, or status-only checks claiming unchanged state. Assertions should detect the
claimed regression rather than mirror implementation structure or prove that a mock returns its
configured value. Preserve intentional public-contract assertions even when they are inexpensive
or mechanically simple.

Use a concrete counterexample or available mutation evidence to challenge a weak assertion.
Distinguish a reasoned prediction from an executed probe. Follow the read-only review permissions
and verification-boundaries.md; do not edit tracked code or add mutation tooling during review.

## Choose the cheapest sufficient boundary

Prefer focused unit tests for pure calculations, normalization, validation, formatting, and state
transitions whose dependencies can be controlled without replacing the behavior under test.
Keep exhaustive input permutations at that owner, with representative wiring checks above it.
Mocks cannot establish the correctness of the boundary they replace.

Retain more expensive tests when cheaper checks cannot safely establish the regression guarantee:

| Guarantee | Boundary that must remain represented |
| --- | --- |
| Routing, binding, serialization, middleware, authentication and CSRF | Actual HTTP pipeline |
| Database constraints, restricted-principal enforcement, query/provider semantics, transactions and races | Real database engine and intended identities; competing operations where required |
| Geometry, focus, history, file input and native downloads | Real browser |
| Migration compatibility, physical recovery, packaging and process/environment behavior | Relevant historical schema, storage, built artifact or runtime |

Similar inputs across these boundaries can protect different failure modes. A SQL constraint test
and HTTP rejection test are not interchangeable; independently wired implementations may each
need coverage. Conversely, repeating a pure matrix through SQL or a browser needs a boundary-specific
reason. Do not demand a unit-test counterpart where it would merely duplicate an already economical
integration check, or a production abstraction solely to enable mocking.

Assess actual cost: fixture startup/disposal, migrations, repeated data creation, process/browser
launch, waits, contention, flakiness, and maintenance coupling. An integration label alone does not
prove expense. Prefer removing unnecessary setup, using existing isolated fixtures or controlled
clocks, and separating responsive geometry from repeated persistence before deleting useful claims.
Preserve isolation, realistic authority, production deadlines, and representative complete journeys.
Use comparable measurements for speedup claims; source inspection can establish redundant work but
not seconds saved. Do not require a benchmark for a trivial duplicate or sum overlapping stage times.

## Make recommendations actionable and proportionate

Before recommending removal or consolidation, name the surviving test and the exact guarantee it
retains. If no sufficient owner exists, recommend moving or strengthening coverage first. Explain
unique assertions or boundary cases that must survive. Avoid arbitrary count-reduction targets,
blanket bans on integration tests, and mandates for exhaustive matrices at every layer.

For material findings, report the affected test, concrete redundant or undetected failure, existing
owner or cheaper sufficient replacement, and evidence/measurement limits. Distinguish keep,
strengthen, move, consolidate, and remove. Summarize the test-value assessment in the Quality report;
a concise no-material-concerns conclusion suffices when supported, not a mandatory per-test ledger.

Apply the council's existing severity and required-fix rules. A demonstrated gap in a critical
regression guarantee, lost unique coverage, or substantiated material avoidable cost can warrant a
required fix. Minor duplication or an unmeasured speed preference is ordinarily nonblocking; do not
invent a product defect or block on test taxonomy alone. Keep unrelated legacy cleanup out of scope.
