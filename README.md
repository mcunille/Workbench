# Workbench™

Workbench is fully open-source software by The White Stag Collection for gemstone and jewelry
hobbyists, collectors, and businesses. It is intended to connect three areas that are often managed
separately: inventory and collections, bookkeeping and accounting, and commerce.

The project is implementing its accepted base architecture in phases. Its React and TypeScript
client and ASP.NET Core API publish as one same-origin release unit. SQL Server persistence,
database-enforced tenant isolation, built-in identity, durable sessions, and explicit database
operations are implemented. Provider infrastructure and Azure deployment remain later phases.

## Develop locally

Install the pinned .NET SDK `10.0.400`, Node.js `26.7.0`, npm `11.19.0`, and PowerShell 7. Docker is
also required for the container smoke test.

Copy `.env.dev.example` to the ignored `.env.dev`, replace every placeholder with a distinct local
secret, start the disposable SQL Server, and perform the one-time bootstrap:

```powershell
Copy-Item .env.dev.example .env.dev
./scripts/test-sql.ps1 -Action Start
./scripts/bootstrap.ps1
```

Keep `.env.dev` only in the worktree that uses it. Never commit it, paste it into agent prompts, or
reuse its setup, web, operator, migrator, or administrator passwords outside local development.
Agents may use the stable local administrator account after the human-controlled bootstrap; they do
not need the setup, operator, or migrator principal for ordinary application development.

Start the API at `http://localhost:5000` after loading the local environment:

```powershell
. ./scripts/dev-env.ps1
dotnet run --project src/Workbench.Server
```

In another terminal, start the client at `http://localhost:5173`; Vite proxies relative `/api` and
`/health` requests to the API:

```powershell
npm ci --prefix src/Workbench.Client
npm run dev --prefix src/Workbench.Client
```

Run locked restores, formatting, contract generation, builds, server and client tests, SQL
migration/browser checks, and the published same-origin smoke test with:

```powershell
./scripts/verify.ps1
```

On a machine with a Linux-container Docker engine, verify the non-root, read-only runtime image and
hardened Compose topology with:

```powershell
./scripts/smoke-container.ps1
```

## Start here

- [Product vision](docs/VISION.md)
- [Design principles](docs/DESIGN-PRINCIPLES.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Database migrations](docs/operations/database-migrations.md)
- [Database backup and restore](docs/operations/database-backup-restore.md)
- [Data and identity threat model](docs/security/data-identity-threat-model.md)
- [Documentation guide](docs/README.md)
- [Contributing](CONTRIBUTING.md)

## Open source

Workbench is licensed under the [GNU Affero General Public License v3.0](LICENSE). It may be used
locally, self-hosted, or provided as a hosted service under the terms of that license.

Workbench is provided without warranty; see sections 15 through 17 of the license.

The project is designed and developed with substantial AI assistance. See the
[AI disclosure](AI-DISCLOSURE.md) for details about how AI tools are used and how human
responsibility is preserved.

The Workbench name and branding are governed separately by the [trademark policy](TRADEMARKS.md).
