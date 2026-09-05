# Workbench client refactoring

Read [client guidance](../../../../src/Workbench.Client/AGENTS.md). Tests are colocated under
`src/Workbench.Client/src`; use descriptive names and Gherkin comments. Characterize pure logic
directly and wired user behavior through components. Mock a feature API boundary when one exists;
transport behavior needs separate API-client tests. Do not mock the module being measured.

Use the existing `src/test/setup.ts` and `vite.config.ts` configuration. The npm scripts preserve
Node's `--no-experimental-webstorage` setting.

From the repository root:

```powershell
npm run test:run --prefix src/Workbench.Client
npm run typecheck --prefix src/Workbench.Client
npm run lint --prefix src/Workbench.Client
```

For focused iterations, append `-- <test-file>` to `test:run`. Use `test:run`, since `test` may
watch indefinitely. Use the checked-in typecheck script so project references are actually built;
a bare `tsc --noEmit` against an empty solution-style config is not equivalent.

## Coverage

At migration time, the client does not declare `@vitest/coverage-v8`. The helper checks for an
installed provider and fails clearly if missing; it does not install packages interactively.
Adding a matching provider and updating the lockfile is separate dependency setup, outside this
skill's behavior-preserving scope. Report this limitation and follow the skill's coverage-shortfall
rule; do not claim a measured percentage without a report.

Once the matching provider is available:

```powershell
pwsh -NoProfile -File .agents/skills/start-refactor/scripts/coverage-gate.ps1 -Stack web -Target src/features/example.ts
```

The helper runs Vitest once, includes shipped `src/**/*.ts` and `src/**/*.tsx` (including unimported
source), and writes Cobertura into a unique temporary directory. Vitest's default test-file
exclusions remain in effect. Verify that requested paths match the report, including extracted
siblings. Covered render lines without assertions do not establish preserved behavior; assess
mutations of the actual branch, fallback, side effect, or asynchronous ordering.

Inspect affected user workflows in the running browser under client guidance. Complete the root
application verification gates before delivery, reporting unavailable checks explicitly.
