# Workbench™

Workbench is fully open-source software by The White Stag Collection for gemstone and jewelry
hobbyists, collectors, and businesses. It is intended to connect three areas that are often managed
separately: inventory and collections, bookkeeping and accounting, and commerce.

The current application provides a private collection notebook: save and find individual pieces,
manage a photograph, correct descriptions, archive and restore records, record acquisition context,
and download collection copies. See the [collection guide](docs/collection.md) for current behavior
and boundaries. Accounting and commerce remain future workflows.

The React client and ASP.NET Core API ship as one same-origin release unit with SQL Server tenant
isolation. See [architecture](docs/ARCHITECTURE.md) for technical contracts and the
[acceptance matrix](docs/operations/production-readiness.md) for dated deployment evidence.

## Setup and installation

Follow the [canonical setup guide](docs/setup.md) to launch an isolated per-worktree database,
API, and built UI with `./scripts/dev-up.ps1`, then test at the reported localhost URL.
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
