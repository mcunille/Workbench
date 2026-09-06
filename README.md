# Workbench™

Workbench is fully open-source software by The White Stag Collection for gemstone and jewelry
hobbyists, collectors, and businesses. It is intended to connect three areas that are often managed
separately: inventory and collections, bookkeeping and accounting, and commerce.

The project is implementing its accepted base architecture in phases. Its React and TypeScript
client and ASP.NET Core API publish as one same-origin release unit. SQL Server persistence,
database-enforced tenant isolation, built-in identity, durable sessions, and explicit database
operations are implemented. Blob and operational providers and deployment infrastructure are
implemented; hosted acceptance remains pending.

## Setup and installation

Follow the [canonical setup guide](docs/setup.md) for prerequisites, safe `.env.dev` creation,
SQL initialization, first login, routine startup, and supported installation paths.
See [Contributing](CONTRIBUTING.md) for verification and change-delivery requirements.

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
