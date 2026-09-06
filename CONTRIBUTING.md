# Contributing to Workbench

Workbench is implementing its accepted architecture in reviewable phases. Contributions that
clarify the product, test its assumptions, improve its design, or deliver an accepted phase are
welcome.

## Development prerequisites

The repository pins .NET SDK `10.0.400`, Node.js `26.7.0`, and npm `11.19.0`. Use PowerShell 7 for
the checked-in scripts and a Linux-container Docker engine for container verification. Do not update
one toolchain pin without updating its locks, CI setup, documentation, and smoke evidence.

Create a worktree-local `.env.dev` from `.env.dev.example`. The file contains credentials and is
ignored by Git; never commit it, copy it into logs or prompts, or share it between installations.
The setup principal is used only by `./scripts/bootstrap.ps1`; routine schema changes use the
migrator principal through `./scripts/migrate.ps1`; the web process receives only the web
connection. Operator and migrator credentials must never be passed to the running web process.

The server and client develop independently after SQL is started and bootstrapped:

```powershell
./scripts/test-sql.ps1 -Action Start
./scripts/bootstrap.ps1
. ./scripts/dev-env.ps1
dotnet run --project src/Workbench.Server
npm ci --prefix src/Workbench.Client
npm run dev --prefix src/Workbench.Client
```

The server listens at `http://localhost:5000`; Vite listens at `http://localhost:5173` and proxies
relative API and health requests. Production does not use CORS or a separate client origin.

Before submitting application changes, run:

```powershell
./scripts/verify.ps1
./scripts/smoke-container.ps1
```

The first command performs locked restores, documentation checks, formatting, OpenAPI client drift
detection, builds, tests, migration drills, browser checks, and published-output probes. The second
requires Docker and verifies a SQL-backed runtime image as non-root and read-only with no Node.js,
source files, setup credential, operator credential, or migrator credential. If Docker is
unavailable, state that limit explicitly; do not report the container gate as passed.

Read the [migration runbook](docs/operations/database-migrations.md) before changing the schema and
the [backup/restore runbook](docs/operations/database-backup-restore.md) before any recovery drill.
Database backups, connection files, password files, recovery links, and `.env.dev` are sensitive
artifacts and must remain outside source control.

Read the [blob and operational provider runbook](docs/operations/blob-and-service-providers.md) when
changing storage, SMTP, workers, retention, or recovery. Blob metadata migrations intentionally reject
destructive down-migration; the rollback gate verifies that guard and the restore path. Run
`BlobRecoveryTests` for paired SQL/blob recovery and `AzureBlobStoreTests` for emulator portability.

## Before proposing a change

1. Read the [product vision](docs/VISION.md) and [design principles](docs/DESIGN-PRINCIPLES.md).
2. Open or join a discussion about a substantial change before investing in an implementation.
3. Write a spec for a change that introduces meaningful product behavior, changes a durable
   contract, or establishes an architectural direction.

Small corrections and documentation improvements do not require a spec.

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
