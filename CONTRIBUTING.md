# Contributing to Workbench

Workbench is implementing its accepted architecture in reviewable phases. Contributions that
clarify the product, test its assumptions, improve its design, or deliver an accepted phase are
welcome.

## Development prerequisites

The repository pins .NET SDK `10.0.400`, Node.js `26.7.0`, and npm `11.19.0`. Use PowerShell 7 for
the checked-in scripts and a Linux-container Docker engine for container verification. Do not update
one toolchain pin without updating its locks, CI setup, documentation, and smoke evidence.

The server and client develop independently:

```powershell
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

The first command performs locked restores, formatting, OpenAPI client drift detection, builds,
tests, and published-output probes. The second requires Docker and verifies the runtime image as
non-root and read-only with no Node.js or source files. If Docker is unavailable, state that limit
explicitly; do not report the container gate as passed.

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
