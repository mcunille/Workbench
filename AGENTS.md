# Repository Guidance

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
