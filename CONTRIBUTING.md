# Contributing to Workbench

Workbench is implementing its accepted architecture in reviewable phases. Contributions that
clarify the product, test its assumptions, improve its design, or deliver an accepted phase are
welcome.

## Development prerequisites

The repository pins .NET SDK `10.0.401`, Node.js `26.7.0`, and npm `11.19.0`. Use PowerShell 7 for
the checked-in scripts and a Linux-container Docker engine for container verification. Do not update
one toolchain pin without updating its locks, CI setup, documentation, and smoke evidence.

Use the [canonical setup and installation guide](docs/setup.md) and `./scripts/dev-up.ps1` for an
isolated per-worktree database, API, and built UI. Rerun after edits and share the reported localhost
URL with the user after exercising the changed workflow. `dev-status.ps1` reports readiness and
source freshness; `dev-down.ps1` preserves test data. Deletion requires `dev-destroy.ps1` with the
exact environment ID. Keep `.dev-environment/secrets/login.txt` and all generated credentials private.
This preview has no HMR or worker; use the documented self-host workflow for worker acceptance.
Existing `.env.dev` installations retain their [manual workflow](docs/operations/manual-development.md).

Before submitting application changes, run:

```powershell
./scripts/verify.ps1
./scripts/smoke-container.ps1
```

The first command performs locked restores, documentation checks, formatting, OpenAPI client drift
detection, builds, tests (including migration drills in the server suite), browser checks, and
published-output probes. Full-gate outcomes and stage/partition timings are retained in `artifacts/verification/<run-id>/`.
The concurrent client stage uses one test worker while .NET formatting/build runs, keeping
DOM test deadlines from competing with additional client workers. Focused client runs retain
the runner defaults. The gate uses two isolated server processes by default; adjust
`-ServerPartitions` (2–4) and `-ServerConcurrency` (1–4) for available Docker resources. Live browser
tests use one worker; fully intercepted UI tests have their own bounded project. Every discovered
server case must pass exactly once. See the
[concurrent gate design](docs/specs/2026-09-09-concurrent-verification-gate.md) for artifact
provenance and the aggregate CI check.
The second requires Docker and verifies a SQL-backed runtime image as non-root and read-only with no Node.js,
source files, setup credential, operator credential, or migrator credential. If Docker is
unavailable, state that limit explicitly; do not report the container gate as passed.
If an existing local Docker network overlaps the smoke fixture's default `172.29` range, select
an unused private prefix with `./scripts/smoke-container.ps1 -ComposeIngressPrefix '172.30'`.
The fixture chooses its /24 within octets 100–239; verify the chosen prefix is available first.

Read the [migration runbook](docs/operations/database-migrations.md) before changing the schema and
the [backup/restore runbook](docs/operations/database-backup-restore.md) before any recovery drill.
Database backups, connection files, password files, recovery links, and `.env.dev` are sensitive
artifacts and must remain outside source control.

Read the [blob and operational provider runbook](docs/operations/blob-and-service-providers.md) when
changing storage, SMTP, workers, retention, or recovery. Blob metadata migrations intentionally reject
destructive down-migration; the rollback gate verifies that guard and the restore path. Run
`BlobRecoveryTests` for paired SQL/blob recovery and `AzureBlobStoreTests` for emulator portability.
For filesystem durability changes, run `bash scripts/test-storage-durability.sh` on Linux with .NET 10
and `strace`. It builds a probe from the current storage source, checks directory sync ordering, and
injects sync errors and interruptions. This tests syscall behavior, not physical power-loss recovery.

Browser checks reuse two distinct authenticated sessions in the live worker's tenant. The dedicated
authentication suite uses a separate tenant and fresh sessions; other UI journeys that sign out
also use uncached sessions. Cookies live only in the disposable browser-run directory and are
removed by the parent test command. The
intercepted project uses synthetic page-owned state and fails on undeclared API requests. Set
`WORKBENCH_BROWSER_UI_WORKERS` to `1` or `2` to compare its concurrency; live concurrency stays at one.
If another checkout is using the default browser port, set `WORKBENCH_BROWSER_PORT` to a free port
before running `npm test --prefix tests/Workbench.BrowserTests` or `./scripts/verify.ps1`.

See [test ownership and cost](tests/README.md) for choosing API, SQL, component, or browser coverage
and [the efficiency design](docs/specs/2026-09-16-test-suite-efficiency.md) for measured changes.
Routine evidence screenshots are disabled; set `WORKBENCH_BROWSER_EVIDENCE_DIRECTORY` to an
external local directory to capture them. Layout assertions and safe failure diagnostics always run.
Existing navigation/menu captures continue to use `WORKBENCH_MENU_EVIDENCE_DIRECTORY`.

## Focused local iteration

Select only the tests affected by your edit while iterating:

```powershell
./scripts/test-focused.ps1 -ServerFilter 'FullyQualifiedName~PhotoProcessorTests'
./scripts/test-focused.ps1 -ClientFiles 'src/main.test.tsx'
./scripts/test-focused.ps1 -BrowserFiles 'inventory.spec.ts'
```

Client and browser selectors are relative to their respective test runner directories and use
Vitest's filename filtering and Playwright's file matching. Supply actual filenames from the
checkout; selectors can be arrays, and selections for different suites can be combined. Empty
selections are rejected, and server filters matching no tests fail. Server tests build current
Release source, and browser tests first build the client and then use the existing SQL/server
startup and cleanup workflow.

Dependencies must already be restored/installed. Add `-InstallDependencies` after changing locks
or on first use to perform locked .NET restore and npm installs for the selected suites. Browser
tests also require Docker and installed Playwright Chromium (see the setup guide). Ordinary
SQL integration tests require Docker; photo-processing tests can run without SQL.

Focused runs are iteration feedback. The full `verify.ps1` and `smoke-container.ps1` delivery gates
above remain required; use `verify.ps1 -SkipDependencyInstall` when npm dependencies are unchanged.
See the [local test iteration design](docs/specs/2026-09-08-local-test-iteration.md) for database
template isolation, measurement results, and the cold-start tradeoff.

## Before proposing a change

Deployment changes also run `./infra/azure/test-parameters.ps1` and
`./scripts/test-compose-proxy.ps1`. CI compiles and lints `infra/azure/main.bicep` with Bicep
`0.46.1`. CI verifies the compiled template's geographic backup settings with
`./infra/azure/test-backup-redundancy.ps1 -TemplateFile <compiled-main.json>`.
These checks do not create Azure resources. The container smoke gate exercises the
production Compose topology using disposable SQL and local test TLS; public DNS/certificates,
real SMTP delivery, and hosted recovery still require their documented operational drills.

1. Read the [product vision](docs/VISION.md) and [design principles](docs/DESIGN-PRINCIPLES.md).
2. Open or join a discussion about a substantial change before investing in an implementation.
3. Write a spec for a change that introduces meaningful product behavior, changes a durable
   contract, or establishes an architectural direction.

Small corrections and documentation improvements do not require a spec.

API changes follow [the API evolution principle](docs/DESIGN-PRINCIPLES.md#13-evolve-apis-deliberately).
All APIs are beta until the first release establishes v1. Prefer compatible changes when they
do not complicate the code. A breaking change requires a clear rationale and explicit approval
before implementation, including during beta. Record affected callers, compatibility alternatives,
stored-data and retry implications, rollout, and rollback in the design. Do not treat approval of
general cleanup as approval of an unstated breaking change.

## Pull requests

- Submit changes through a pull request; do not push directly to the protected default branch.
- Keep each pull request focused on one coherent change.
- Explain the user need, the chosen approach, and how the result was verified.
- Update current documentation when a change makes an accepted spec true.
- Commit regenerated API declarations whenever the server OpenAPI contract changes.

Specs describe the reasoning behind a change. The vision and design-principles documents describe
the project's current direction and must remain understandable without reconstructing spec history.

## License

By contributing, you agree that your contributions will be licensed under the repository's
[GNU Affero General Public License v3.0](LICENSE).

The source-code license does not grant rights to the Workbench name or branding. See the
[trademark policy](TRADEMARKS.md).

## Browser failure evidence

Failed browser tests print a `Safe browser layout evidence` path. In GitHub Actions,
open the failed run's **Summary → Artifacts → browser-failure-diagnostics** (also linked
in the job summary). Each `failure-*` directory contains `layout.json` and `layout.png`.
Match the directory to the failing test's console output. JSON element indices match
screenshot labels; structural selectors identify elements without retaining their IDs
or text. Coordinates are viewport-relative; red outlines indicate horizontal overflow.

The PNG is a reconstructed layout diagram, not a photograph of the application.
Only standard HTML element types, structural selectors, bounding rectangles, viewport
size and scroll position are retained. This helps diagnose overlap, clipping and sizing;
it cannot diagnose colors, typography, image content, or text wrapping within an element.
Page text, attributes, CSS, images, URLs, raw errors, request/response data, cookies and
storage state are excluded by construction. Raw Playwright traces are disabled; local
`test-results` error contexts are never uploaded or advertised as CI evidence by the
line reporter. Tests use isolated synthetic data.
The verification-evidence artifact also retains browser/results.json with overall status,
duration and per-attempt hashed identity, source filename/line, status, retry and timing.
It excludes titles, raw errors, console output, annotations and attachments.

Capture uses the built-in Playwright page; failures on manually created secondary pages
may show an unrelated primary page. Capture is best effort: closed/crashed pages can have
no evidence. At most ten failures, 1,000 visible elements from the first 10,000 DOM nodes, 20 selector ancestors, 1920×1080
pixels and 2 MiB per failure are retained. The JSON reports truncation of element counts;
the original viewport remains recorded even when the diagram is clipped. Artifacts expire
after seven days. Each normal browser run clears the dedicated diagnostic directory first.
Capture failures do not replace the test failure, and the parent browser runner still owns
SQL/session-file cleanup. Raw CI console assertions retain their existing behavior; they
are not copied into this diagnostic artifact.

Run the browser isolation, synthetic capture/privacy, and deliberate-failure contracts after
installing browser npm dependencies and Chromium:

```powershell
node --test tests/Workbench.BrowserTests/safe-diagnostics.test.mjs `
  tests/Workbench.BrowserTests/browser-isolation.test.mjs `
  tests/Workbench.BrowserTests/browser-isolation-wiring.test.mjs `
  tests/Workbench.BrowserTests/diagnostic-budget.test.mjs
```

The deliberate-failure cleanup check also requires PowerShell and Docker; it starts no
application or database. Concurrent capture retains at most ten safe diagnostic directories.
