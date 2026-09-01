# Workbench™

Workbench is fully open-source software by The White Stag Collection for gemstone and jewelry
hobbyists, collectors, and businesses. It is intended to connect three areas that are often managed
separately: inventory and collections, bookkeeping and accounting, and commerce.

The project is implementing its accepted base architecture in phases. Its application foundation
contains an independently developed React and TypeScript client and ASP.NET Core API that publish as
one same-origin release unit. SQL Server persistence, identity, tenancy, provider infrastructure,
and Azure deployment remain later phases.

## Develop locally

Install the pinned .NET SDK `10.0.400`, Node.js `24.20.0`, npm `11.19.0`, and PowerShell 7. Docker is
also required for the container smoke test.

Start the API at `http://localhost:5000`:

```powershell
dotnet run --project src/Workbench.Server
```

In another terminal, start the client at `http://localhost:5173`; Vite proxies relative `/api` and
`/health` requests to the API:

```powershell
npm ci --prefix src/Workbench.Client
npm run dev --prefix src/Workbench.Client
```

Run locked restores, formatting, contract generation, builds, server and client tests, and the
published same-origin smoke test with:

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
