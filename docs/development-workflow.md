# Development workflow

Use the installed superpowers skills for design, planning, execution, debugging, and internal
review. Read the applicable skill rather than reproducing its process here. Root
[AGENTS.md](../AGENTS.md) defines the repository's explicit adaptations and authorization
boundaries; [CONTRIBUTING.md](../CONTRIBUTING.md) owns application verification gates.

## Design and planning

Use superpowers:brainstorming to classify design work and complete the selected path's reviews.
A bounded change needs the short in-chat design approval. Architectural work needs the written
spec review and the subsequent implementation-plan handoff; approval of an earlier stage does
not approve an unseen artifact. Once a particular artifact is approved, preserve that approval.

Keep durable specs in `docs/specs/`, including changes requiring a spec under CONTRIBUTING.md.
Update living architecture and product documentation when implementation makes the design current.
Keep plans in the ignored `docs/superpowers/plans/` directory and use the selected execution skill's
ignored workspace for ledgers, task briefs, and review packages. Plans and execution scratch are
not committed. Follow superpowers:writing-plans for plan content, self-review, and the user's
execution-method choice.

## Execution and verification

Use superpowers:executing-plans or superpowers:subagent-driven-development according to the
approved handoff. Use superpowers:systematic-debugging for failures, including its escalation
conditions, and superpowers:verification-before-completion for completion claims.

Reuse an existing isolated workspace rather than creating a nested worktree. Start and refresh
this checkout's preview through `scripts/dev-up.ps1` as documented in [setup](setup.md). Keep
credentials private and preserve other checkouts' environments and unrelated edits.

Repository TDD, characterization coverage for behavior-preserving refactors, Gherkin comments,
mutation testing, and stack-specific test boundaries remain applicable. Component tests may mock
the feature API boundary as described in the client guidance; real SQL tests establish persistence,
concurrency, and tenant-isolation behavior. Run the applicable CONTRIBUTING.md delivery gates;
focused task tests do not replace them. Documentation-only changes use affected documentation and
guidance checks without requiring an application build or container smoke run.

For superpowers dispatches, follow the selected skill's model-selection rules and Codex tool
adaptation. Resolve role tiers against the current tool's available models and supported reasoning
efforts, specifying both explicitly. Do not pin changing model names in repository guidance.
Explicit user model choices and runtime constraints still apply. The repository PR Review Council
retains its own specialist assignments. Coordinate shared Git-index operations; the primary agent
owns integration and publication. Serialize edits and tests that share files, databases, or build
outputs.

## Internal review and local skills

Follow the selected superpowers execution skill's review and fix loop. Review the complete task
or branch range, not just the last commit, and verify the integrated result against current source.
Use the skill's fallback when required review tooling is unavailable and disclose missing coverage.

Use `start-refactor` for bounded behavior-preserving refactors after the applicable superpowers
design handoff. Its characterization, mutation, and coverage requirements supplement the process.
Use `handle-pr-feedback` for the author's feedback round: inspect and validate feedback first,
then complete applicable superpowers design/planning handoffs before editing. Its invocation
authorizes in-scope delivery, while its exact-preview gate governs collaboration writes.
Use `impeccable` for UI design and implementation within the selected superpowers design process.

Internal implementation review does not invoke the PR Review Council or authorize GitHub review
publication. Use `review-pr` for a user-requested review from the reviewer's seat and preserve its
read-only contract and publication gate. `handle-pr-feedback` retains its separate permissions for
comments, issues, replies, and thread resolution.

## Delivery

Verified implementation is delivered through a ready-for-review PR; this is the already-selected
finishing option under AGENTS.md. Keep the branch and worktree for feedback. Merge, production
operations, and separately gated collaboration writes require their own authorization.

## Guidance maintenance

Keep repository-specific contracts here and process details in the owning skills. Check guidance
changes against representative scenarios, including bounded work, architectural handoffs, review
feedback, blocked verification, and PR delivery. Distinguish static scenario checks from actual
agent experiments, and report verification limits.
