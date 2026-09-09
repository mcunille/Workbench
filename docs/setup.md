# Setup and installation

Use the per-worktree preview for local development and agent changes. Each checkout gets its own
SQL container, persistent SQL/blob volumes, network, credentials, and browser session cookies.
The API serves the built UI at one loopback URL. Closing the terminal leaves the preview running.

## Prerequisites

The launcher currently supports **Windows, PowerShell 7.4 or later, Git, and Docker Desktop with a
running Linux-container engine** capable of running the pinned SQL Server amd64 image. Run commands
from the repository root in `pwsh`. Docker builds the pinned application toolchain; host .NET and
Node installations are needed for local tests, not for launching the preview.

```powershell
$PSVersionTable.PSVersion
git --version
docker version
docker compose version
docker info --format '{{.OSType}}'
```

The last command must return `linux`. The scripts locate Docker at
`$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin\docker.exe` when it is missing from PATH.
For manual checks on this Windows machine, invoke that path with PowerShell's `&` operator.
Use a clean shell without `COMPOSE_*` overrides. Do not copy another checkout's `.dev-environment`
directory; it records ownership of that checkout and its Docker resources.

## Start and test a preview

```powershell
./scripts/dev-up.ps1 -TenantName 'Local Workbench' -AdminEmail 'admin@example.test'
```

These are also the defaults, so `./scripts/dev-up.ps1` works without prompts. Tenant/email options
apply when creating an environment; subsequent starts preserve the saved identity and test data.
The command builds current source, initializes the database with the existing migration and
bootstrap commands, starts the API/UI, and checks readiness before printing its URL.

Open the reported `http://localhost:<port>` address. Docker chooses an available loopback port;
SQL has no published host port. The initial login is stored privately in
`.dev-environment/secrets/login.txt`. Read it with a private editor; do not print or attach it to
logs, chat, or pull requests. The command reports the file path, not the password.

Sign in, exercise the changed workflow, and reload to confirm the saved state. Agents should
give the user the reported URL and describe what they actually tested. Readiness verifies the
server/database and built UI response; it does not replace testing the requested workflow.

## Refresh, inspect, and stop

```powershell
./scripts/dev-up.ps1
./scripts/dev-status.ps1
./scripts/dev-down.ps1
```

Rerun `dev-up` after source changes. The workflow uses **built previews**, without Vite HMR
or a background worker. It reuses an unchanged ready build and preserves data when refreshing.
Build inputs include tracked and untracked, non-ignored files under `src/Workbench.Server`,
`src/Workbench.Database`, and `src/Workbench.Client`, plus the Dockerfile and explicit root build
configuration. Generated outputs, `.env` files, and preview secrets are excluded. A source
fingerprint catches edits during the build and makes `dev-status` report stale source.

`dev-status` reports the environment ID, readiness, URL, source revision, dirty state, and whether
build inputs changed. Add `-Json` to any lifecycle command for version 1 structured output;
`dev-up` and `dev-status` return a nonzero exit code when the preview is not ready. Always use the
reported URL: the previous port is preferred on restart, but a conflict can require a new port.

`dev-down` stops only this environment's services and retains its volumes, credentials, and state.
Use `dev-up` to resume. Independent previews can remain signed in simultaneously in one browser.
The state directory is ignored and restricted to the local account; it is not a backup.

## Failures and deliberate deletion

On interrupted startup, preserve `.dev-environment` and rerun `dev-up` after resolving the reported
failure. Existing credentials and committed bootstrap state are reused. Newer or divergent database
migration history is rejected before migration. Do not edit migration-history rows, copy state
between worktrees, or delete unfamiliar Docker resources to make a retry succeed.

To remove this environment's database, blobs, containers, network, and local credentials, first
inspect `dev-status`, then supply its exact environment ID:

```powershell
./scripts/dev-destroy.ps1 -EnvironmentId 'dev-<exact-id-from-dev-status>'
```

This is destructive. Preserve any data you need before running it. Resource ownership is checked
before removal; a mismatch requires investigation. A later `dev-up` creates a fresh environment.
The lock file and a non-secret `destroyed` state record remain so concurrent commands cannot
acquire different locks. Docker's reusable image/build cache is also retained.

## Existing installations and verification

Existing `.env.dev` users can follow the retained [manual development workflow](operations/manual-development.md).
For a retained Windows QA service with worker and localhost HTTPS, use the automated
[local self-host installation](operations/local-self-host.md). For production requirements, start
with the [production operations audit](operations/production-readiness.md). Development SQL and
loopback previews are not production installations.

For local verification install the repository-pinned .NET SDK **10.0.401**, Node.js **26.7.0**, and
npm **11.19.0**, and install Playwright Chromium before browser tests:

```powershell
npm ci --prefix tests/Workbench.BrowserTests --ignore-scripts --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Browser dependency install failed.' }
node tests/Workbench.BrowserTests/node_modules/@playwright/test/cli.js install chromium
if ($LASTEXITCODE -ne 0) { throw 'Browser install failed.' }
./scripts/verify.ps1
./scripts/smoke-container.ps1
```

See [Contributing](../CONTRIBUTING.md) for focused iteration and delivery gates, and the
[approved preview design](specs/2026-09-09-worktree-development-environments.md) for lifecycle,
ownership, and recovery contracts. A successful preview does not establish that these gates passed.
