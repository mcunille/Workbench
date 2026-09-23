# Repository Guidance

## Scope and completion

- Use the applicable superpowers skills for design, planning, execution, debugging, and internal review. Complete the selected workflow's design and planning handoffs before implementation; a request for a bounded change does not skip its short design review. Preserve approval of an already reviewed artifact, but do not treat it as approval of a later artifact that has not been presented.
- Continue until the requested behavior is implemented, relevant checks pass, and any requested running application has been inspected. Fix failures caused by the change. Report unrelated failures and concrete blockers accurately.
- Preserve the repository skills' separate approval gates for publishing review comments and other collaboration writes. Merging and production operations require explicit authorization; implementation approval alone does not authorize them.
- Once requested implementation is complete and verified, commit the scoped changes and open or update a ready-for-review pull request. This is the repository's preselected option for superpowers:finishing-a-development-branch; do not ask the integration-menu question again. Create a draft only when requested. This authorizes implementation delivery, not publication of review comments or other separately gated collaboration writes.
- Keep incidental cleanup small, directly related, and low-risk. Surface broader refactors and design tradeoffs separately with evidence and affected locations; obtain the required approval before filing issues. Preserve unrelated working-tree changes, and inspect logs and diffs before concluding work in another checkout is safe to discard.

## Verification

- Use TDD for new or changed behavior, including bug fixes: write a focused test first, run it, and confirm it fails because the intended behavior is missing rather than because of a setup or compilation error. Write the minimal implementation that makes it pass, then refactor while keeping tests green. Cover important failure cases as well as successful behavior.
- For behavior-preserving refactors, add missing characterization coverage before refactoring and keep existing tests green; do not invent a failing requirement for unchanged behavior.
- Structure tests with Gherkin comments: `GIVEN` the initial condition, optional `AND` conditions, `WHEN` the action, and `THEN` the expected behavior, with optional `AND` outcomes. Describe domain conditions and observable results next to the relevant setup, action, and assertions. Use comments in the existing test framework; a separate Gherkin framework is not required.
- Use mutation testing to assess whether tests detect meaningful changes to behavior, especially business rules, authorization, validation, and state transitions. Start with affected code using available mutation tooling. Investigate surviving mutants and strengthen tests when they reveal coverage or assertion gaps; document equivalent mutants and other justified exclusions rather than chasing a blanket 100% score. If tooling is unavailable, report the coverage limitation; do not claim mutation testing ran. Broader mutation runs and tooling or CI integration should be scoped separately based on measured runtime.
- Changes that do not affect behavior, such as documentation-only or formatting-only edits, do not automatically require new tests. Run repository-required checks and affected verification. Broaden or repeat testing when changes, failures, or unresolved risks justify it.
- Verify artifacts built from the current source; do not use stale binaries or an unverified `--no-build` result as evidence. For cross-layer application changes, run and exercise the affected workflow and provide the local URLs. State unavailable checks and remaining coverage limits explicitly.
- Follow [CONTRIBUTING.md](CONTRIBUTING.md) for application verification gates and API contract generation, and [docs/README.md](docs/README.md) for authoritative architecture and product guidance. Consult the relevant document when changing its contract rather than requiring a full documentation tour for every edit.

## Planning artifacts

- Keep durable specifications in `docs/specs/`, including those produced by superpowers:brainstorming. Follow `CONTRIBUTING.md` for changes needing a durable spec and the selected superpowers workflow for its review handoffs.
- Use superpowers:writing-plans when an implementation plan is required. Follow its task structure, self-review, and user handoff, including plan review and execution-method selection before implementation. Bounded work follows the brainstorming skill's short in-chat design path.
- Save implementation plans in the ignored `docs/superpowers/plans/` directory. They are temporary working artifacts; do not commit them. Keep execution ledgers and task artifacts in the selected execution skill's ignored workspace.
- Read [the development workflow](docs/development-workflow.md) for repository integration rules, verification ownership, and local skill routing. It supplements superpowers rather than defining a second development process.

## Development credentials

- Start and refresh this checkout's isolated preview with `./scripts/dev-up.ps1`; use `dev-status.ps1` and `dev-down.ps1` for inspection and non-destructive stop. Follow `docs/setup.md`, inspect the affected workflow in the browser, and give the user the reported URL. Never reuse another checkout's environment state or delete its resources. `dev-destroy.ps1` is an explicit data-deletion operation, not routine cleanup.
- Preview login credentials are stored in the protected, ignored `.dev-environment/secrets/login.txt`. Read them privately for browser verification; never print or include them in chat or artifacts.
- Agents may load the ignored `.env.dev` file through `scripts/dev-env.ps1` for local Workbench development.
- Never print, log, commit, or include credential values in command arguments, test output, diffs, or generated artifacts.
- Use the migration credential only for explicit migration commands; the web process must use the web credential.

## Change integration

- When visual evidence helps reviewers understand the result, include a `VERIFICATION EVIDENCE` section in the PR description with a representative Playwright screenshot or short recording of the affected workflow. Capture the current source, use non-sensitive sample data, and caption the evidence with the revision, state shown, and any verification limits. Visual evidence supplements the required checks; it does not establish behavior that was not exercised.
- Keep verification images and recordings outside the repository and Git history. Upload them as GitHub attachments using a supported GitHub CLI version with `gh pr edit --attach` (and `--body-file` when updating the description); do not use browser debugging for uploads. Preserve the existing PR description and verify that the resulting attachment URL appears in it. If attachment support or permission is unavailable, report the blocker rather than committing the media.
- Before opening or updating a pull request, consolidate development-only migrations introduced by the current change into one migration per coherent release change. Preserve dependency ordering, custom SQL, security controls, data transformations, and rollback guards; update the final designer/model snapshot, migration-history assertions, and schema-version references, then verify fresh-database creation and upgrade from the merged PR base schema. Keep separate migrations only when a staged deployment, backfill, or release compatibility boundary requires them, and explain that reason in the PR. Migrations become durable when their PR merges into main; never rewrite merged migrations. Applying an unmerged migration to a local, retained, or shared preview does not prevent consolidation or create a supported upgrade baseline. Preserving preview data is a separate local operation and does not authorize deletion or migration-history resets. Follow the migration runbook for the durable-baseline rule.
- After requested changes are complete and verification passes, push the working branch and open a pull request against its base branch.
- Do not merge the pull request without explicit user authorization.
