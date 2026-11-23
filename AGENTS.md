# Repository Guidance

## Workflow and authority

- Design, implementation, review: apply the [five-step algorithm](docs/development-workflow.md#first-principles-the-five-step-algorithm) in order. Changing safeguards requires the applicable approval.
- Before implementation, complete the applicable superpowers [design and planning handoffs](docs/development-workflow.md#design-and-planning). Bounded changes require short design approval; approval covers the presented artifact, not later unseen artifacts.
- After verified implementation, commit scoped changes, push the branch, and open or update a ready-for-review PR against its base branch. This preselects superpowers:finishing-a-development-branch's PR option; skip the integration-menu question. Create a draft only when requested.
- Preserve skills' separate approval gates for review publication and other collaboration writes. Delivery approval does not authorize those writes. Merge and production operations require explicit user authorization.
- Keep incidental cleanup small, directly related, and low-risk. Surface broader refactors and design tradeoffs separately with evidence and affected locations; obtain the required approval before filing issues. Preserve unrelated working-tree changes, and inspect logs and diffs before concluding work in another checkout is safe to discard.
- Preserve other checkouts' resources. Keep credential values out of chat, logs, command arguments, test output, diffs, generated artifacts, and Git history.

## Task routes

- Execution, debugging, completion claims: follow [execution and verification](docs/development-workflow.md#execution-and-verification).
- Module design, internal review, PR review, feedback, UI work, or test authoring: follow [skill routing](docs/development-workflow.md#internal-review-and-skills). For `codebase-design`, `review-pr`, or `handle-pr-feedback`, also follow [installed skill ownership](docs/development-workflow.md#skill-installation-and-ownership).
- Documentation audits or cleanup: use `doc-audit` and read [audit scope, bookkeeping, and skill ownership](docs/documentation-audits.md).
- Behavior changes, refactors, test edits, or verification: follow [test method](tests/README.md#test-method), including current-source evidence. Documentation-only and formatting-only changes use its affected-check guidance.
- Application changes or API generation: follow [CONTRIBUTING.md](CONTRIBUTING.md). Architecture or product contracts: locate the owner through [docs/README.md](docs/README.md).
- Preview lifecycle: follow [setup](docs/setup.md). Credentials: follow [credential use](docs/setup.md#credential-use); existing `.env.dev` installations use [manual development](docs/operations/manual-development.md).
- Schema changes: follow [migration authoring and validation](docs/operations/database-migrations.md#authoring-and-validating-a-migration), including pre-PR consolidation and preview-data preservation.
- Visual PR evidence: follow [verification evidence](CONTRIBUTING.md#verification-evidence).
- Before executing or modifying checkout scripts, tool configuration, Impeccable, or hooks: review the [developer tooling trust boundary](docs/development-workflow.md#developer-tooling-trust-boundary).
