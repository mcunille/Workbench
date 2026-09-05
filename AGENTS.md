# Repository Guidance

## Scope and completion

- Proceed with clearly requested, bounded changes through implementation and verification. Ask for design approval before architectural changes, material changes to public contracts, or decisions with significant unresolved tradeoffs. Once a design is approved, continue through the agreed completion criteria without repeating approval requests.
- Continue until the requested behavior is implemented, relevant checks pass, and any requested running application has been inspected. Fix failures caused by the change. Report unrelated failures and concrete blockers accurately.
- Preserve the repository skills' separate approval gates for publishing review comments and other collaboration writes. Merging and production operations require explicit authorization; implementation approval alone does not authorize them.

## Verification

- For bug fixes, add a regression test when it meaningfully demonstrates the defect; confirm it fails for the expected reason before fixing it when practical. For features, test the affected behavior and important failure cases.
- Documentation, formatting, generated output, and configuration changes do not automatically require new tests. Run repository-required checks and affected verification. Broaden or repeat testing when changes, failures, or unresolved risks justify it.

## Planning artifacts

- Put specifications in `docs/specs/`.
- Implementation plans are temporary working artifacts. Do not commit them.
- After writing an implementation plan, recommend one execution option with a brief reason and start the recommended option automatically instead of asking the user to choose. For small or sequential plans, use inline execution; for multiple independent tasks, use subagent-driven execution only when subagents are available and permitted.
