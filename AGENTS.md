# Repository Guidance

## Scope and completion

- Proceed with clearly requested, bounded changes through implementation and verification. Ask for design approval before architectural changes, material changes to public contracts, or decisions with significant unresolved tradeoffs. Once a design is approved, continue through the agreed completion criteria without repeating approval requests.
- Continue until the requested behavior is implemented, relevant checks pass, and any requested running application has been inspected. Fix failures caused by the change. Report unrelated failures and concrete blockers accurately.
- Preserve the repository skills' separate approval gates for publishing review comments and other collaboration writes. Merging and production operations require explicit authorization; implementation approval alone does not authorize them.
- Once requested implementation is complete and verified, commit the scoped changes and open or update a ready-for-review pull request. Create a draft only when requested. This authorizes implementation delivery, not publication of review comments or other separately gated collaboration writes.
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

- Put specifications in `docs/specs/`.
- Implementation plans are temporary working artifacts. Do not commit them.
- After writing an implementation plan, recommend one execution option with a brief reason and start the recommended option automatically instead of asking the user to choose. For small or sequential plans, use inline execution; for multiple independent tasks, use subagent-driven execution only when subagents are available and permitted.

## Development credentials

- Agents may load the ignored `.env.dev` file through `scripts/dev-env.ps1` for local Workbench development.
- Never print, log, commit, or include credential values in command arguments, test output, diffs, or generated artifacts.
- Use the migration credential only for explicit migration commands; the web process must use the web credential.

## Change integration

- After requested changes are complete and verification passes, push the working branch and open a pull request against its base branch.
- Do not merge the pull request without explicit user authorization.
