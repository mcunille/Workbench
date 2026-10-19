# Development workflow

Use the installed superpowers skills for design, planning, execution, debugging, and internal
review. Read the applicable skill rather than reproducing its process here. This guide owns
Workbench's workflow adaptations; root [AGENTS.md](../AGENTS.md) defines shared scope, authorization,
and delivery requirements. [CONTRIBUTING.md](../CONTRIBUTING.md) owns application verification gates.

## First principles: the five-step algorithm

Apply a first-principles approach, often called "Elon's algorithm" from SpaceX, during design,
implementation, and review. Work through these steps in strict order:

1. **Question every requirement.** Challenge assumptions, rules, and constraints. Identify who
   owns each requirement and the evidence or domain truth that justifies it. If no one can defend
   a requirement, remove it through the applicable approval process.
2. **Delete anything unnecessary.** The best part is no part. Remove unnecessary parts, steps,
   and processes aggressively before improving them. The calibration rule is that if you do not
   end up restoring roughly 10% of what you removed, you may not have deleted enough. Treat this
   as a heuristic for challenging conservative deletion, not a deletion or restoration quota;
   restore what evidence shows is needed.
3. **Simplify and optimize.** Only simplify what remains after deletion. The most common mistake
   of a smart engineer is to optimize a thing that should not exist in the first place. Prefer
   the smallest coherent design that satisfies the remaining, justified requirements.
4. **Accelerate cycle time.** Speed up the remaining process after deletion and simplification.
   Measure the bottleneck and verify that faster feedback or execution preserves correctness.
   Accelerating earlier only makes bad processes fail faster.
5. **Automate last.** Automation is the final step, not the first. Automate only the justified,
   simplified process after its cycle time has been addressed. Automating or optimizing too
   early amplifies waste instead of eliminating it.

Use the reasoning in the existing design and review artifacts; do not add a separate ceremony
for each step. When new evidence challenges an earlier requirement, revisit that step before
continuing downstream. Follow the [design principles](DESIGN-PRINCIPLES.md) when defending domain
invariants, including financial history, tenant isolation, authorization, and data ownership.
Challenging a requirement does not itself authorize removing an existing safeguard, verification
gate, or approval boundary. Propose such changes explicitly and obtain the applicable approval;
keep unrelated deletion outside the approved scope.

## Design and planning

Use superpowers:brainstorming to classify design work and complete the selected path's reviews.
A bounded change needs the short in-chat design approval. Architectural work needs the written
spec review and the subsequent implementation-plan handoff; approval of an earlier stage does
not approve an unseen artifact. Once a particular artifact is approved, preserve that approval.

Keep durable specs in `docs/specs/`, including those produced by superpowers:brainstorming and
changes requiring a spec under CONTRIBUTING.md.
Update living architecture and product documentation when implementation makes the design current.
Keep plans in the ignored `docs/superpowers/plans/` directory and use the selected execution skill's
ignored workspace for ledgers, task briefs, and review packages. Plans and execution scratch are
not committed. When an implementation plan is required, use superpowers:writing-plans for its task
structure, self-review, and user handoff, including plan review and execution-method selection
before implementation.

## Execution and verification

Use superpowers:executing-plans or superpowers:subagent-driven-development according to the
approved handoff. Use superpowers:systematic-debugging for failures, including its escalation
conditions, and superpowers:verification-before-completion for completion claims.

Reuse an existing isolated workspace rather than creating a nested worktree. For local preview
lifecycle and browser verification, follow [setup](setup.md).

Follow [test method](../tests/README.md#test-method) and stack-specific test boundaries. Component
tests may mock the feature API boundary as described in the client guidance; real SQL tests establish
persistence, concurrency, and tenant-isolation behavior.

For superpowers dispatches, follow the selected skill's model-selection rules and Codex tool
adaptation. Resolve role tiers against the current tool's available models and supported reasoning
efforts, specifying both explicitly. Do not pin changing model names in repository guidance.
Explicit user model choices and runtime constraints still apply. The repository PR Review Council
retains its own specialist assignments. Coordinate shared Git-index operations; the primary agent
owns integration and publication. Serialize edits and tests that share files, databases, or build
outputs.

## Skill installation and ownership

`codebase-design`, `review-pr`, and `handle-pr-feedback` are maintained in the private
`mcunille/skills` repository and installed at user scope in `~/.agents/skills/`. Workbench
does not vendor these three skills or their shared GitHub references. Install complete skill
directories, including references, licenses, provenance, and tests. Avoid same-name project
copies: Codex does not merge duplicate skills or provide a reliable project override.

The reviewed migration baseline is skills revision
`a0aad81c38f30312d56f198c81a756a8771ad0f1`. With authorized access to that repository,
install the three skills using the library's pinned installer:

```powershell
npx --yes skills@1.7.2 add 'mcunille/skills#a0aad81c38f30312d56f198c81a756a8771ad0f1' --agent codex --skill codebase-design review-pr handle-pr-feedback --global --copy
```

Inspect existing user and project installations before accepting replacement of any destination.
Alternatively, copy the three complete directories from a clean local clone at that reviewed
revision into `~/.agents/skills/`, preserving existing installations until differences have been
reviewed. Review later source revisions before updating; do not synchronize from a moving branch
automatically. Refresh or restart Codex if needed and confirm the three skills appear at user scope.
Missing skills must be reported before a workflow that requires them; do not silently substitute
another workflow or restore vendored copies. Installing these skills does not grant collaboration
write permissions or replace Workbench's required Superpowers handoffs.

The library owns its generic skill regression tests. Run the review-boundary suite from the
installed `review-pr` directory with `pwsh -NoProfile -File tests/ReviewBoundary.Tests.ps1`.
Workbench CI and application verification do not require private-library access or a personal
skill installation. Workbench-specific refactoring coverage tooling, Impeccable launchers and
their tests remain here with the four remaining local skills.

Generic skills discover Workbench policies through root AGENTS.md and its conditional references
rather than embedding repository-specific contracts.

## Internal review and skills

Follow the selected superpowers execution skill's review and fix loop. Review the complete task
or branch range, not just the last commit, and verify the integrated result against current source.
Use the skill's fallback when required review tooling is unavailable and disclose missing coverage.

Use [test-audit](../.agents/skills/test-audit/SKILL.md) when authoring or reviewing tests,
or for requested test audits. Its authoring gate supplements TDD and the
[test-ownership guidance](../tests/README.md); audit discovery remains read-only until
implementation is authorized. Read its campaign reference only for subsystem-wide audits.
Use `start-refactor` for bounded behavior-preserving refactors after the applicable superpowers
design handoff. Its characterization, mutation, and coverage requirements supplement the process.
Use `handle-pr-feedback` for the author's feedback round: inspect and validate feedback first,
then complete applicable superpowers design/planning handoffs before editing. Its invocation
authorizes in-scope delivery, while its exact-preview gate governs collaboration writes.
Use `impeccable` for UI design and implementation within the selected superpowers design process.
Use [doc-audit](../.agents/skills/doc-audit/SKILL.md) for requested documentation audits and
cleanup: identify obsolete or duplicated material and preserve durable decisions in maintained
docs before retiring specs. Audit discovery is read-only; cleanup follows existing authorization.

Internal implementation review does not invoke the PR Review Council or authorize GitHub review
publication. Use `review-pr` for a user-requested review from the reviewer's seat and preserve its
read-only contract and publication gate. `handle-pr-feedback` retains its separate permissions for
comments, issues, replies, and thread resolution.

## Developer tooling trust boundary

Treat scripts and tool configuration in a checkout as untrusted until reviewed, including
changes on a branch of an otherwise trusted repository. Workbench intentionally has no
repository-local automatic Codex hooks: post-edit and stop events must not execute the
mutable Impeccable launchers in `.agents/skills/impeccable/scripts/`. A check inside a
launcher, or disabling its detector in Impeccable configuration, happens after that
launcher has already begun executing and does not protect this boundary.

Use Impeccable manually after reviewing the checkout's executable tooling and configuration.
The skill's hookless workflow supports manual detection over the changed targets; automatic
post-edit notices and the stop-time design pass are intentionally unavailable. Do not restore
project hook manifests through `impeccable hooks on` or tooling repair/update commands.

Impeccable Live is disabled in this repository. Its page-injected UI exposes a reusable
credential for the local helper's source-reading and editing APIs. Both launchers reject
`live` and `live-*` arguments, regardless of case or position, before engine lookup or
download. This also disables the helper-backed critique overlay. Use ordinary browser
inspection, the standalone detector, and agent-led source edits instead. A target literally
named `live` or beginning with `live-` must be expressed as a path such as `./live`.

Do not bypass this restriction by invoking an external engine directly or restoring Live
setup during a tooling update. The restriction contains the repository integration; it
does not repair external engines or terminate existing helper processes. Re-enabling Live
requires a reviewed upstream boundary that keeps credentials and filesystem authority
outside the inspected page. Verify the launcher contract with
`node --test tests/Workbench.BuildTests/ImpeccableLaunchers.test.mjs`; CI runs it on Windows
and Linux.

Any future automatic hooks must run trusted tooling maintained outside the checkout. If
that tooling executes repository code, it must verify the code against independently trusted
provenance before execution; moving only the launcher is insufficient. Hook approval and trust
enforcement belong outside the mutable repository as well. Removing the committed registrations
closes the current automatic execution path, but cannot prevent an untrusted branch from
introducing new hooks or changing repository guidance. Review those changes before trusting them.

## Delivery

Follow root [delivery and authorization requirements](../AGENTS.md#workflow-and-authority).
Keep the branch and worktree for feedback. For visual PR evidence, follow
[verification evidence](../CONTRIBUTING.md#verification-evidence); for schema changes, complete
[migration authoring and validation](operations/database-migrations.md#authoring-and-validating-a-migration).

## Guidance maintenance

Keep repository-specific contracts here and process details in the owning skills. Check guidance
changes against representative scenarios, including bounded work, architectural handoffs, review
feedback, blocked verification, and PR delivery. Distinguish static scenario checks from actual
agent experiments, and report verification limits.
