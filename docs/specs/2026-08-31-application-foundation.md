# Application foundation and single release unit

**Status:** Accepted

**Issue:** [#9](https://github.com/mcunille/Workbench/issues/9)

**Parent architecture:**
[`2026-08-31-base-application-architecture.md`](2026-08-31-base-application-architecture.md)

## Summary

Workbench will establish its first runnable application as three independently testable projects: an
ASP.NET Core server, a React and TypeScript client, and server integration tests. Development keeps
the client and server processes independent. Publishing combines the compiled client and server into
one same-origin ASP.NET Core release unit and one hardened container image.

This phase creates only the application and delivery foundation. SQL persistence, identity, tenant
enforcement, blob providers, SMTP, durable operations, telemetry exporters, and Azure resources are
owned by later phases and must not be represented by placeholder implementations here.

## Goals

- Pin supported .NET and Node LTS toolchains and commit deterministic dependency locks.
- Establish explicit server, client, and integration-test project boundaries.
- Generate the TypeScript API contract from the server's OpenAPI description.
- Serve API and client traffic from one origin in published output.
- Keep API misses and failures within stable API error contracts rather than returning the SPA.
- Give liveness and dependency readiness distinct, independently verifiable semantics.
- Build a minimal, non-root, read-only production image without Node.js or source files.
- Make local and CI verification repeatable from checked-in commands.

## Non-goals

- SQL Server, Entity Framework Core, migrations, tenant data, or authentication.
- Blob, email, rate-limit, background-work, or telemetry provider implementations.
- Azure infrastructure or a production TLS reverse proxy.
- Domain modules, workflows, navigation, or final visual design.
- A generalized health-check framework beyond the registration and tagging seam needed by later
  dependencies.

## Toolchains and dependency policy

The repository pins the .NET 10 LTS SDK in `global.json` with feature-band roll-forward disabled and
pins Node.js 24 LTS in both `.nvmrc` and `.node-version`. The client package declares the same Node
major in `engines` and declares the npm version used to create `package-lock.json` through
`packageManager`.

.NET projects use central package version management and committed `packages.lock.json` files.
Normal verification restores NuGet packages with locked mode and installs JavaScript packages with
`npm ci`. CI fails when a lock file is inconsistent rather than rewriting it.

Toolchain pins are compatibility commitments, not instructions to bundle build tools into the
runtime image. Upgrades require updating pins, locks, CI setup, documentation, and smoke evidence in
one change.

## Repository structure

```text
Workbench.slnx
global.json
Directory.Build.props
Directory.Packages.props
.nvmrc
.node-version
src/
  Workbench.Server/
    Workbench.Server.csproj
    Program.cs
    Contracts/
    wwwroot/
  Workbench.Client/
    package.json
    package-lock.json
    src/
    openapi/
tests/
  Workbench.Server.IntegrationTests/
scripts/
  verify.ps1
  smoke-container.ps1
Dockerfile
compose.yaml
```

`Workbench.Server` is the composition root and production web host. It exposes application contracts
but contains no persistence or provider implementation in this phase. `Workbench.Client` owns the
browser application, API generation, and client tests. `Workbench.Server.IntegrationTests` exercises
the actual HTTP pipeline through `WebApplicationFactory`; it does not replace routing or middleware
with mocks.

## HTTP and typed API boundary

Application API routes live beneath `/api`. The initial `GET /api/system` endpoint returns a small
named response contract containing the application name and release version. It proves the complete
typed boundary without inventing domain behavior.

ASP.NET Core emits an OpenAPI document during the server build. `openapi-typescript` converts that
document into a checked-in generated TypeScript declaration, and `openapi-fetch` consumes it through
a small client-owned API module. Generated output is never hand-edited. The generation script is
deterministic, and verification regenerates the declaration and fails on a tracked diff. The React
application therefore cannot silently drift from the server's published path, method, status, or
payload shape.

During development Vite proxies relative `/api` and `/health` requests to the documented local
ASP.NET Core address. Production uses only relative URLs and has no CORS dependency.

## Errors, routing, and SPA fallback

The server uses ASP.NET Core Problem Details as the stable error shape. Production exception
responses use status `500`, media type `application/problem+json`, a stable title and type, and a
request trace identifier. They do not disclose exception messages, stack traces, filesystem paths,
configuration, or secrets. Development diagnostics may remain available through ordinary local
logging but do not change the response contract asserted by production-mode tests.

An explicit terminal `/api/{**path}` handler returns a Problem Details `404`. It is registered before
the client fallback so `/api/not-a-route` can never return `index.html`, including in published
output. Non-API `GET` and `HEAD` paths that do not match a static asset fall back to the React shell.
Other unmatched methods do not receive the SPA document.

## Health semantics

The unauthenticated endpoints are:

- `GET /health/live`: reports only whether the process and HTTP pipeline are alive. It excludes
  dependency checks.
- `GET /health/ready`: reports the liveness check plus all checks tagged `ready`. Any required
  dependency failure makes readiness unhealthy without changing liveness.

Both endpoints return a small stable JSON contract and `200` when healthy. Readiness returns `503`
when a required check is unhealthy. Integration tests add a deliberately failing readiness-tagged
check to prove the two endpoints diverge. Later phases register SQL, key, blob, email, or migration
checks through the readiness tag rather than changing endpoint semantics.

## Client shell

The client uses React, TypeScript strict mode, and Vite. Its initial accessible shell identifies the
application and renders the typed `/api/system` result. Loading and failure states remain explicit;
an API failure does not produce an empty page or expose raw server error details. Vitest and Testing
Library cover the successful and failed boundary behavior using request interception at the HTTP
edge rather than mocking generated client internals.

This phase intentionally avoids a component framework, router, global state library, or speculative
navigation. Later product work can select those when real interface behavior exists.

## Build, publish, and runtime image

Ordinary client and server development remain independent:

- `dotnet run --project src/Workbench.Server` starts the API.
- `npm run dev --prefix src/Workbench.Client` starts Vite with the development proxy.
- Each project has focused build and test commands.

`dotnet publish` is the canonical release-unit build. A publish target performs a locked client
install and production build, then includes only `dist` artifacts beneath the server's published
`wwwroot`. A switch allows the container build to supply an already compiled client without running
Node twice, but it does not permit published output without the client shell.

The Dockerfile uses separate pinned Node SDK, .NET SDK, and ASP.NET runtime stages. The final image:

- runs as an explicitly declared non-root numeric user;
- listens on unprivileged port `8080`;
- contains only the ASP.NET Core runtime and published application files;
- contains no Node or npm executable, package cache, project source, test files, or build SDK;
- writes only to an explicitly mounted temporary path; and
- supports graceful ASP.NET Core termination.

`compose.yaml` binds the application to loopback for local smoke testing, uses a read-only root
filesystem, mounts a `tmpfs` for temporary data, drops all Linux capabilities, sets
`no-new-privileges`, and defines the HTTP health probe. It adds no database or durable volume in this
phase.

## Verification and automation

`scripts/verify.ps1` is the contributor and CI entry point. It performs, in order:

1. toolchain-version validation;
2. locked NuGet restore and `npm ci`;
3. .NET formatting verification;
4. generated OpenAPI TypeScript drift verification;
5. server build and integration tests;
6. client formatting or linting, type checking, unit tests, and production build; and
7. publish verification of the combined release unit.

`scripts/smoke-container.ps1` builds the image, runs it with a read-only root filesystem and temporary
writable mount, verifies the React shell, API response, API `404`, liveness, and readiness, then
inspects the image configuration and contents. It fails if the configured user is root or absent, or
if Node.js, npm, client source, server source, or test projects are present. Cleanup runs in `finally`
and removes only the named temporary container and image created by the script.

GitHub Actions runs locked restore, formatting, contract generation, build, tests, publish checks,
and the container smoke script. CodeQL analyzes Actions, C#, and JavaScript/TypeScript. C# uses a
manual build with the pinned .NET SDK; JavaScript/TypeScript analysis installs the pinned Node
toolchain. Workflow permissions remain least-privileged.

## Documentation

The root README and contributor guide document prerequisites, pinned versions, independent
development commands, local URLs, full verification, published execution, and container smoke
testing. `docs/ARCHITECTURE.md` changes from “implementation has not begun” to an accurate phase
status only after verification succeeds. Documentation must distinguish locally verified behavior
from Docker checks skipped because Docker is unavailable.

## Security and failure behavior

- The release path never copies JavaScript build-time environment secrets into the client bundle.
- Server errors and health output expose no configuration or exception internals.
- SPA routing cannot mask unknown API routes.
- The container has no root requirement, writable application tree, shell-dependent health probe,
  or ambient Linux capabilities.
- CI runs dependency and static analysis over both application languages.
- A final scoped security diff scan covers source, build, scripts, container, Compose, workflows, and
  phase documentation. Every reportable finding must be fixed or explicitly left unresolved in the
  handoff; the issue is not complete while a reportable finding remains unresolved.

No durable application state exists in this phase, so backup, schema migration, and data rollback do
not apply. Source rollback restores the previous documentation-only repository; image rollback uses
the prior immutable image once releases exist.

## Alternatives considered

### ASP.NET Core client template with development proxy coupling

Rejected. It can create the shell quickly, but makes server commands implicitly own the client
development lifecycle and weakens the explicit independent-project boundary.

### Separately deployed static client and API

Rejected for the initial release. It introduces cross-origin authentication and coordinated release
concerns before there is evidence that static hosting is needed. The independent client project and
relative URLs preserve that future option.

### Handwritten TypeScript response interfaces

Rejected. They compile even when the server contract changes and therefore do not enforce the typed
boundary. OpenAPI generation makes drift mechanically visible.

### Node.js in the final image to serve the client

Rejected. ASP.NET Core already owns the public origin and static-file pipeline. A second runtime
increases image size and attack surface without adding capability.

## Acceptance criteria

The phase is implemented when:

1. pinned, locked restores work from a clean checkout;
2. server integration tests and client unit tests have demonstrated red-green behavior and pass;
3. the client consumes a generated typed contract for `/api/system`;
4. published output serves the React shell and API from one origin;
5. `/api/not-a-route` returns API Problem Details `404`, never the SPA document;
6. a failing readiness dependency returns `503` while liveness remains `200`;
7. the runtime image is non-root, read-only, and contains no Node.js or source files;
8. formatting, generation drift, build, tests, publish checks, container smoke checks, CI, and CodeQL
   pass; and
9. the final scoped security review has no unresolved reportable findings.

## Residual risks and deferred decisions

- The initial readiness seam has no external dependency until phase #10; its contract is tested with
  an integration-only failing check.
- Docker verification requires a Linux-container engine. A machine without Docker can verify source
  and published output but cannot satisfy the container acceptance criteria.
- OpenAPI generation adds a checked-in derived file. The drift gate is required to keep that file
  trustworthy.
- Exact UI composition, browser routing, accessibility policy, and end-to-end browser tooling remain
  decisions for the first product-facing workflow.
