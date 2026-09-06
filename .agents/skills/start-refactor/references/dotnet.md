# Workbench .NET refactoring

Read [server guidance](../../../../src/Workbench.Server/AGENTS.md) and
[test guidance](../../../../tests/AGENTS.md) for the target. Both server and database code are built
through `Workbench.slnx`; the current test project is
`tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj`.

## Characterization and checks

Place server characterization tests in that test project, following its existing structure.
Test business rules directly and endpoint behavior through HTTP. Use real disposable SQL Server
for transactions, constraints, tenant isolation, and competing operations. Do not substitute a
mock or development connection for persistence behavior. The suite uses Testcontainers.MsSql and
requires a running Linux-container Docker engine. Tests must keep credentials out of output.

From the repository root:

```powershell
dotnet test tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj
```

Add `--filter` for focused iterations; run the suite for the baseline and final affected-stack
check. Build current source after every edit. A verified build followed by `--no-build` in the
repository verification script is valid; an old build used to evade a file lock is not. If a local
process locks outputs, identify the process belonging to this task before stopping it.

Do not assume `InternalsVisibleTo` exists. Prefer the existing public surface and HTTP harness;
justify any narrow test-access change under the skill's characterization rules.

## Coverage

The test project already references `coverlet.collector`. The helper builds and runs it with JSON
coverage, then converts that report to Cobertura while deduplicating physical source lines and
preserving branch identities. The merger also accepts multiple reports from the same source/build
configuration if test projects are added later; update the runner explicitly at that point.

```powershell
pwsh -NoProfile -File .agents/skills/start-refactor/scripts/coverage-gate.ps1 -Stack api -Target SessionService.cs
```

Check the printed file list and exit code. A failing suite or missing report cannot pass. Do not
combine reports from different source revisions. Keep mutation edits out of coverage artifacts
used as final evidence. Record unavailable Docker or collector execution as a limitation.

After helper changes, run its independent fixture/runner tests:

```powershell
pwsh -NoProfile -File .agents/skills/start-refactor/tests/coverage-gate.tests.ps1
```

These validate coverage accounting and orchestration; they do not run the application or SQL suite.
Final application verification remains governed by CONTRIBUTING.md.
