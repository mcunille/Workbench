# Workbench Application Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a tested React and ASP.NET Core application shell that runs through one origin,
publishes as one non-root container image, and is verified by local scripts and CI.

**Architecture:** Keep the React client and ASP.NET Core server as separate projects while publishing
the client into the server's static assets. The server exposes `/api`, liveness, and readiness
endpoints; Vite proxies `/api` during development. This plan establishes the release unit and build
boundaries without introducing SQL, identity, tenancy, blob storage, or Azure resources prematurely.

**Tech Stack:** .NET SDK 10.0.400 targeting .NET 10 LTS; C# 14; Node.js 24.20.0 LTS; npm 11;
React 19.2.8; TypeScript 7.0.2; Vite 8.2.2; `@vitejs/plugin-react` 6.1.0; Vitest 4.1;
xUnit; ASP.NET Core integration testing; Docker Compose; GitHub Actions.

**Spec:** `docs/specs/2026-08-31-base-application-architecture.md`

**Tracking:** [Architecture rollout #8](https://github.com/mcunille/Workbench/issues/8) and
[application foundation #9](https://github.com/mcunille/Workbench/issues/9).

## Global Constraints

- The client and server are separate projects but ship as one same-origin web deployable.
- The HTTP API is rooted at `/api`; frontend code uses relative API URLs only.
- The web process stores no authoritative state in memory or its writable filesystem.
- The runtime container runs as a non-root user and contains no Node.js SDK or source tree.
- `/health/live` reports only process liveness; `/health/ready` is the dependency-readiness surface.
- Production errors do not expose exception details or stack traces to the browser.
- Package manifests and lock files are committed; CI uses locked or clean restore commands.
- Build warnings fail the build. Nullable reference types and implicit usings are enabled.
- This phase adds no SQL, identity, tenant, attachment, SMTP, telemetry-export, or Azure service
  implementation. Those are separate plans named below.
- Use PowerShell 7 (`pwsh`) for repository scripts on Windows.

## Scope Decomposition

The accepted architecture contains four reviewable implementation phases:

1. **Application foundation — this plan:** toolchain, solution, API/client shell, health endpoints,
   one-origin publication, container, local scripts, CI, and CodeQL language coverage.
2. **Platform data and identity:** SQL Server, migrations, `TenantId` enforcement, database-level
   isolation, built-in accounts, durable sessions, revocation, and authorization policies.
3. **Storage and operations:** blob metadata/providers, email, OpenTelemetry, durable background
   work, backup/restore, and security-state recovery.
4. **Azure deployment:** Bicep, Container Apps, Azure SQL, Azure Blob Storage, managed identities,
   staging smoke tests, image provenance, budgets, and release promotion.

Each phase must end with runnable software and its own plan. Do not pull work from phases 2–4 into
this plan to make an interface look more complete.

## File Map

| Path | Responsibility |
|---|---|
| `global.json` | Pin the supported .NET SDK feature band. |
| `.nvmrc` | Pin the Node.js LTS development line. |
| `Directory.Build.props` | Repository-wide C# compiler and build rules. |
| `Workbench.slnx` | Solution membership for source and test projects. |
| `src/Workbench.Web/Workbench.Web.csproj` | ASP.NET Core host and client publish integration. |
| `src/Workbench.Web/Program.cs` | Composition root and middleware/endpoint ordering only. |
| `src/Workbench.Web/System/SystemEndpoints.cs` | Anonymous system-status endpoint. |
| `src/Workbench.Web/Health/HealthEndpointExtensions.cs` | Liveness/readiness endpoint mapping. |
| `src/Workbench.Web/Errors/GlobalExceptionHandler.cs` | Stable production problem response. |
| `src/Workbench.Web/Security/SecurityHeaderExtensions.cs` | Browser response-header baseline. |
| `src/Workbench.Client/` | React, TypeScript, Vite, and client tests. |
| `src/Workbench.Client/src/api/system.ts` | Typed client for `/api/system/status`. |
| `src/Workbench.Client/src/App.tsx` | Minimal application shell and status presentation. |
| `tests/Workbench.Web.IntegrationTests/` | In-process HTTP behavior tests. |
| `src/Workbench.HealthProbe/` | Dependency-free in-container readiness probe. |
| `Dockerfile` | Reproducible multi-stage application image. |
| `compose.yaml` | Loopback-only local container verification; not a production deployment. |
| `scripts/verify.ps1` | One local verification entrypoint. |
| `scripts/dev.ps1` | Coordinated API and Vite development startup. |
| `.github/workflows/ci.yml` | Build, test, publish, container, and dependency checks. |
| `.github/workflows/codeql.yml` | Actions, C#, and JavaScript/TypeScript CodeQL coverage. |

---

### Task 1: Scaffold the solution and prove the API boundary

**Files:**
- Create: `global.json`
- Create: `.nvmrc`
- Create: `Directory.Build.props`
- Create: `Workbench.slnx`
- Create: `src/Workbench.Web/Workbench.Web.csproj`
- Create: `src/Workbench.Web/Program.cs`
- Create: `src/Workbench.Web/System/SystemStatusResponse.cs`
- Create: `src/Workbench.Web/System/SystemEndpoints.cs`
- Create: `tests/Workbench.Web.IntegrationTests/Workbench.Web.IntegrationTests.csproj`
- Create: `tests/Workbench.Web.IntegrationTests/SystemEndpointsTests.cs`

**Interfaces:**
- Consumes: none.
- Produces: `SystemEndpoints.MapSystemEndpoints(IEndpointRouteBuilder)` and
  `SystemStatusResponse(string Status, string Service)`; HTTP `GET /api/system/status` returning
  `200 application/json` with `{ "status": "ok", "service": "Workbench" }`.

- [ ] **Step 1: Pin the toolchain and create the project skeleton**

Create `global.json`:

```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

Create `.nvmrc` containing:

```text
24.20.0
```

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <NuGetAudit>true</NuGetAudit>
    <NuGetAuditMode>all</NuGetAuditMode>
    <NuGetAuditLevel>moderate</NuGetAuditLevel>
    <WarningsAsErrors>$(WarningsAsErrors);NU1901;NU1902;NU1903;NU1904</WarningsAsErrors>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>
</Project>
```

Run:

```powershell
dotnet new sln -n Workbench --format slnx
dotnet new web -n Workbench.Web -o src/Workbench.Web --framework net10.0
dotnet new xunit -n Workbench.Web.IntegrationTests -o tests/Workbench.Web.IntegrationTests --framework net10.0
dotnet sln Workbench.slnx add src/Workbench.Web/Workbench.Web.csproj
dotnet sln Workbench.slnx add tests/Workbench.Web.IntegrationTests/Workbench.Web.IntegrationTests.csproj
dotnet add tests/Workbench.Web.IntegrationTests/Workbench.Web.IntegrationTests.csproj reference src/Workbench.Web/Workbench.Web.csproj
dotnet add tests/Workbench.Web.IntegrationTests/Workbench.Web.IntegrationTests.csproj package Microsoft.AspNetCore.Mvc.Testing --version 10.0.11
dotnet restore Workbench.slnx
dotnet restore Workbench.slnx --locked-mode
```

Commit both generated `packages.lock.json` files. After each intentional project or dependency
change, regenerate and review locks once, then immediately prove `dotnet restore --locked-mode`;
verification, build, test, format, publish, and CodeQL commands must not restore implicitly. Delete
template test files replaced by the named test below. Do not create application class-library
projects until a real module needs one.

- [ ] **Step 2: Write the failing endpoint test**

Create `tests/Workbench.Web.IntegrationTests/SystemEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Workbench.Web.IntegrationTests;

public sealed class SystemEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public SystemEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Status_returns_the_public_service_contract()
    {
        var response = await _client.GetAsync("/api/system/status");
        var body = await response.Content.ReadFromJsonAsync<SystemStatusContract>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new SystemStatusContract("ok", "Workbench"), body);
    }

    private sealed record SystemStatusContract(string Status, string Service);
}
```

Add `public partial class Program;` after `app.Run()` in the template `Program.cs` so the test project
can create the host. Do not map the endpoint yet.

- [ ] **Step 3: Run the test and verify the RED gate**

Run:

```powershell
dotnet test tests/Workbench.Web.IntegrationTests/Workbench.Web.IntegrationTests.csproj --filter Status_returns_the_public_service_contract --no-restore
```

Expected: FAIL because `/api/system/status` returns `404 Not Found`.

- [ ] **Step 4: Implement the minimal system endpoint**

Create `src/Workbench.Web/System/SystemStatusResponse.cs`:

```csharp
namespace Workbench.Web.System;

public sealed record SystemStatusResponse(string Status, string Service);
```

Create `src/Workbench.Web/System/SystemEndpoints.cs`:

```csharp
namespace Workbench.Web.System;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/system/status",
                () => TypedResults.Ok(new SystemStatusResponse("ok", "Workbench")))
            .AllowAnonymous()
            .WithName("GetSystemStatus");

        return endpoints;
    }
}
```

Replace `Program.cs` with:

```csharp
using Workbench.Web.System;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapSystemEndpoints();
app.Run();

public partial class Program;
```

- [ ] **Step 5: Run focused and full server verification**

Run:

```powershell
dotnet restore Workbench.slnx --locked-mode
dotnet test tests/Workbench.Web.IntegrationTests/Workbench.Web.IntegrationTests.csproj --filter Status_returns_the_public_service_contract --no-restore
dotnet build Workbench.slnx --configuration Release --no-restore
dotnet test Workbench.slnx --configuration Release --no-build
dotnet format Workbench.slnx --verify-no-changes --no-restore
```

Expected: all commands exit `0`; the focused test reports one passing test.

- [ ] **Step 6: Commit the API foundation**

```powershell
git add global.json .nvmrc Directory.Build.props Workbench.slnx src tests
git commit -m "Build ASP.NET application foundation"
```

---

### Task 2: Build the typed React shell through TDD

**Files:**
- Create: `src/Workbench.Client/package.json`
- Create: `src/Workbench.Client/package-lock.json`
- Create: `src/Workbench.Client/tsconfig*.json`
- Create: `src/Workbench.Client/vite.config.ts`
- Create: `src/Workbench.Client/index.html`
- Create: `src/Workbench.Client/src/main.tsx`
- Create: `src/Workbench.Client/src/App.tsx`
- Create: `src/Workbench.Client/src/App.css`
- Create: `src/Workbench.Client/src/api/system.ts`
- Create: `src/Workbench.Client/src/api/system.test.ts`

**Interfaces:**
- Consumes: `GET /api/system/status` from Task 1.
- Produces: `SystemStatus`, `fetchSystemStatus(fetcher?: typeof fetch): Promise<SystemStatus>`, and
  a React shell that renders loading, ready, and unavailable states.

- [ ] **Step 1: Scaffold with pinned packages and scripts**

Run with Node.js 24.20.0 active:

```powershell
npm create vite@8.2.2 src/Workbench.Client -- --template react-ts
Set-Location src/Workbench.Client
npm install --save-exact react@19.2.8 react-dom@19.2.8
npm install --save-dev --save-exact typescript@7.0.2 vite@8.2.2 @vitejs/plugin-react@6.1.0 vitest@4.1.0
npm pkg set engines.node=">=24.20.0 <25"
npm pkg set scripts.test="vitest run"
npm pkg set scripts.check="tsc -b --pretty false && vite build && vitest run"
Set-Location ../..
```

Keep the generated `package-lock.json`. Remove generated logos, counters, and example styles.

- [ ] **Step 2: Write the failing typed-client tests**

Create `src/Workbench.Client/src/api/system.test.ts`:

```typescript
import { describe, expect, it, vi } from 'vitest'
import { fetchSystemStatus } from './system'

describe('fetchSystemStatus', () => {
  it('returns the validated system status', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({ status: 'ok', service: 'Workbench' }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    await expect(fetchSystemStatus(fetcher)).resolves.toEqual({
      status: 'ok',
      service: 'Workbench',
    })
    expect(fetcher).toHaveBeenCalledWith('/api/system/status', {
      credentials: 'same-origin',
      headers: { accept: 'application/json' },
    })
  })

  it('rejects unsuccessful responses', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 503 }))

    await expect(fetchSystemStatus(fetcher)).rejects.toThrow('System status request failed: 503')
  })
})
```

- [ ] **Step 3: Run the client RED gate**

Run:

```powershell
npm --prefix src/Workbench.Client test -- src/api/system.test.ts
```

Expected: FAIL because `src/api/system.ts` does not exist.

- [ ] **Step 4: Implement the typed client and application states**

Create `src/Workbench.Client/src/api/system.ts`:

```typescript
export interface SystemStatus {
  status: 'ok'
  service: string
}

export async function fetchSystemStatus(fetcher: typeof fetch = fetch): Promise<SystemStatus> {
  const response = await fetcher('/api/system/status', {
    credentials: 'same-origin',
    headers: { accept: 'application/json' },
  })

  if (!response.ok) {
    throw new Error(`System status request failed: ${response.status}`)
  }

  const value: unknown = await response.json()
  if (!isSystemStatus(value)) {
    throw new Error('System status response was invalid')
  }

  return value
}

function isSystemStatus(value: unknown): value is SystemStatus {
  if (typeof value !== 'object' || value === null) return false
  const candidate = value as Record<string, unknown>
  return candidate.status === 'ok' && typeof candidate.service === 'string'
}
```

Create `src/Workbench.Client/src/App.tsx`:

```tsx
import { useEffect, useState } from 'react'
import { fetchSystemStatus } from './api/system'
import './App.css'

type LoadState = 'loading' | 'ready' | 'unavailable'

export default function App() {
  const [loadState, setLoadState] = useState<LoadState>('loading')

  useEffect(() => {
    let active = true

    void fetchSystemStatus()
      .then(() => {
        if (active) setLoadState('ready')
      })
      .catch(() => {
        if (active) setLoadState('unavailable')
      })

    return () => {
      active = false
    }
  }, [])

  const statusText = {
    loading: 'Starting Workbench…',
    ready: 'Workbench is ready',
    unavailable: 'Workbench is unavailable',
  }[loadState]

  return (
    <main>
      <h1>Workbench</h1>
      <p role="status">{statusText}</p>
    </main>
  )
}
```

Keep `App.css` minimal and do not add a design system in this phase.

Configure `vite.config.ts`:

```typescript
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:5000',
      '/health': 'http://localhost:5000',
    },
  },
  test: {
    environment: 'node',
  },
})
```

- [ ] **Step 5: Run client verification**

Run:

```powershell
npm --prefix src/Workbench.Client test -- src/api/system.test.ts
npm --prefix src/Workbench.Client run check
```

Expected: the two focused tests pass; TypeScript, production build, and all Vitest tests exit `0`.

- [ ] **Step 6: Commit the client shell**

```powershell
git add src/Workbench.Client
git commit -m "Build React application shell"
```

---

### Task 3: Publish one origin with health and safe errors

**Files:**
- Modify: `src/Workbench.Web/Workbench.Web.csproj`
- Modify: `src/Workbench.Web/Program.cs`
- Create: `src/Workbench.Web/Health/HealthEndpointExtensions.cs`
- Create: `src/Workbench.Web/Errors/GlobalExceptionHandler.cs`
- Create: `src/Workbench.Web/Errors/ErrorEndpoints.cs`
- Create: `src/Workbench.Web/Security/SecurityHeaderExtensions.cs`
- Create: `tests/Workbench.Web.IntegrationTests/HostingBehaviorTests.cs`

**Interfaces:**
- Consumes: React `dist/**` from Task 2 and server host from Task 1.
- Produces: `GET /health/live`, `GET /health/ready`, same-origin SPA fallback, and stable
  `application/problem+json` responses for unhandled API exceptions.
- Produces: an application-owned browser header baseline plus HSTS for non-development HTTPS
  requests; the later ingress/proxy plan may add compatible edge enforcement but must not weaken it.

- [ ] **Step 1: Write failing hosting behavior tests**

Create `HostingBehaviorTests.cs` with these exact assertions:

```csharp
[Theory]
[InlineData("/health/live")]
[InlineData("/health/ready")]
public async Task Health_endpoints_are_successful(string path)
{
    var response = await _client.GetAsync(path);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}

[Fact]
public async Task Unknown_api_route_does_not_fall_back_to_the_spa()
{
    var response = await _client.GetAsync("/api/not-a-route");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task Responses_include_the_browser_security_baseline()
{
    var response = await _client.GetAsync("/api/system/status");

    Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    Assert.Equal(
        "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; object-src 'none'",
        response.Headers.GetValues("Content-Security-Policy").Single());
    Assert.Equal(
        "camera=(), microphone=(), geolocation=()",
        response.Headers.GetValues("Permissions-Policy").Single());
    Assert.Equal(
        "same-origin",
        response.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
}

[Fact]
public async Task Hsts_is_enabled_for_non_development_https_requests()
{
    using var client = _factory
        .WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production))
        .CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

    var response = await client.GetAsync("/api/system/status");
    Assert.Contains("max-age=", response.Headers.GetValues("Strict-Transport-Security").Single());
}

[Fact]
public async Task Hsts_is_disabled_for_development_requests()
{
    using var client = _factory
        .WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Development))
        .CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

    var response = await client.GetAsync("/api/system/status");
    Assert.False(response.Headers.Contains("Strict-Transport-Security"));
}
```

Use the same `WebApplicationFactory<Program>` fixture pattern as Task 1, retain both `_factory` and
its default `_client`, and add `using Microsoft.AspNetCore.Hosting;` for `UseEnvironment`.

- [ ] **Step 2: Run the hosting RED gate**

Run:

```powershell
dotnet test tests/Workbench.Web.IntegrationTests/Workbench.Web.IntegrationTests.csproj --filter HostingBehaviorTests --no-restore
```

Expected: FAIL because the health endpoints are not mapped.

- [ ] **Step 3: Implement health, error, and fallback ordering**

Create `HealthEndpointExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Workbench.Web.Health;

public static class HealthEndpointExtensions
{
    public static IServiceCollection AddWorkbenchHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkbenchHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
        });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
        });
        return endpoints;
    }
}
```

Create `src/Workbench.Web/Security/SecurityHeaderExtensions.cs`:

```csharp
namespace Workbench.Web.Security;

public static class SecurityHeaderExtensions
{
    public static IApplicationBuilder UseWorkbenchSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] =
                    "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; " +
                    "form-action 'self'; object-src 'none'";
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                return Task.CompletedTask;
            });

            await next();
        });
}
```

Create `src/Workbench.Web/Errors/GlobalExceptionHandler.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Workbench.Web.Errors;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception _,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            "Unhandled request failure. TraceIdentifier: {TraceIdentifier}",
            httpContext.TraceIdentifier);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
            },
        });
    }
}
```

Create `src/Workbench.Web/Errors/ErrorEndpoints.cs`:

```csharp
namespace Workbench.Web.Errors;

public static class ErrorEndpoints
{
    public static IEndpointRouteBuilder MapTestingErrorEndpoint(
        this IEndpointRouteBuilder endpoints,
        IHostEnvironment environment)
    {
        if (environment.IsEnvironment("Testing"))
        {
            endpoints.MapGet("/api/test/error", ThrowTestException);
        }

        return endpoints;
    }

    private static IResult ThrowTestException() =>
        throw new InvalidOperationException("sensitive test exception");
}
```

Add a test using a factory configured with `UseEnvironment("Testing")`. Assert the response is
`500` with media type `application/problem+json`, contains `An unexpected error occurred.`, and does
not contain `sensitive test exception` or a stack trace.

Order `Program.cs` as follows:

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddWorkbenchHealthChecks();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseWorkbenchSecurityHeaders();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapWorkbenchHealthChecks();
app.MapSystemEndpoints();
app.MapTestingErrorEndpoint(app.Environment);
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
```

Keep this order exact so `/api/{**path}` never falls through to the SPA document. Include
`using Workbench.Web.Errors;`, `using Workbench.Web.Health;`, `using Workbench.Web.Security;`, and
`using Workbench.Web.System;`. The generic log event deliberately omits the exception object;
central redaction and controlled diagnostic capture must exist before later phases may export richer
exception details.

- [ ] **Step 4: Wire the React build into `dotnet publish`**

Add this target to `Workbench.Web.csproj`:

```xml
<PropertyGroup>
  <ClientRoot>../Workbench.Client/</ClientRoot>
</PropertyGroup>

<Target Name="BuildClient" BeforeTargets="ComputeFilesToPublish"
        Condition="'$(SkipClientBuild)' != 'true'">
  <Exec WorkingDirectory="$(ClientRoot)" Command="npm ci" />
  <Exec WorkingDirectory="$(ClientRoot)" Command="npm run check" />
  <ItemGroup>
    <ClientDist Include="$(ClientRoot)dist/**/*" />
    <ResolvedFileToPublish Include="@(ClientDist)"
                           RelativePath="wwwroot/%(RecursiveDir)%(Filename)%(Extension)"
                           CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Target>
```

- [ ] **Step 5: Verify server behavior and published assets**

Run:

```powershell
dotnet test Workbench.slnx --configuration Release --no-restore
dotnet publish src/Workbench.Web/Workbench.Web.csproj --configuration Release --no-restore --output artifacts/publish
Test-Path artifacts/publish/wwwroot/index.html
```

Expected: all tests pass, publish exits `0`, and `Test-Path` prints `True`.

- [ ] **Step 6: Commit one-origin publishing**

```powershell
git add src/Workbench.Web tests/Workbench.Web.IntegrationTests
git commit -m "Publish the React and API host together"
```

---

### Task 4: Build and smoke-test the non-root container

**Files:**
- Create: `.dockerignore`
- Create: `Dockerfile`
- Create: `compose.yaml`
- Create: `src/Workbench.HealthProbe/Workbench.HealthProbe.csproj`
- Create: `src/Workbench.HealthProbe/Program.cs`

**Interfaces:**
- Consumes: `dotnet publish` contract and health endpoints from Task 3.
- Produces: image `workbench:local`, container port `8080`, Compose service `workbench`, and a
  healthcheck against `/health/ready`.

- [ ] **Step 1: Establish the container test precondition**

Run:

```powershell
docker version
docker compose version
```

Expected: both commands exit `0`. If Docker is unavailable, stop this task and install or enable a
Docker-compatible engine through an operator-approved action; do not mark the task complete using
only a Dockerfile review.

- [ ] **Step 2: Write the multi-stage Dockerfile**

Create a dependency-free health probe that can run inside the minimal ASP.NET runtime image:

```powershell
dotnet new console -n Workbench.HealthProbe -o src/Workbench.HealthProbe --framework net10.0
dotnet sln Workbench.slnx add src/Workbench.HealthProbe/Workbench.HealthProbe.csproj
dotnet restore Workbench.slnx
dotnet restore Workbench.slnx --locked-mode
```

Replace `src/Workbench.HealthProbe/Program.cs` with:

```csharp
if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint))
{
    return 2;
}

try
{
    using var client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(3),
    };
    using var response = await client.GetAsync(endpoint);
    return response.IsSuccessStatusCode ? 0 : 1;
}
catch (HttpRequestException)
{
    return 1;
}
catch (TaskCanceledException)
{
    return 1;
}
```

Commit the generated `src/Workbench.HealthProbe/packages.lock.json` with the other NuGet locks.

Resolve and review the registry digest for each base before writing the file:

```powershell
docker buildx imagetools inspect node:24.20.0-bookworm-slim
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0
docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0
```

Use this stage structure, but replace every `FROM` image with its reviewed
`tag@sha256:<64-lowercase-hex-digest>` reference. Retain the readable tag so dependency-update tools
can discover new releases; the digest, not the tag, is build authority.

```dockerfile
FROM node:24.20.0-bookworm-slim AS client-build
WORKDIR /src/src/Workbench.Client
COPY src/Workbench.Client/package.json src/Workbench.Client/package-lock.json ./
RUN npm ci
COPY src/Workbench.Client/ ./
RUN npm run check

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
COPY global.json Directory.Build.props Workbench.slnx ./
COPY src/Workbench.Web/Workbench.Web.csproj src/Workbench.Web/packages.lock.json src/Workbench.Web/
COPY src/Workbench.HealthProbe/Workbench.HealthProbe.csproj src/Workbench.HealthProbe/packages.lock.json src/Workbench.HealthProbe/
RUN dotnet restore src/Workbench.Web/Workbench.Web.csproj --locked-mode \
    && dotnet restore src/Workbench.HealthProbe/Workbench.HealthProbe.csproj --locked-mode
COPY src/Workbench.Web/ src/Workbench.Web/
COPY src/Workbench.HealthProbe/ src/Workbench.HealthProbe/
COPY --from=client-build /src/src/Workbench.Client/dist/ src/Workbench.Web/wwwroot/
RUN dotnet publish src/Workbench.Web/Workbench.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:SkipClientBuild=true
RUN dotnet publish src/Workbench.HealthProbe/Workbench.HealthProbe.csproj \
    --configuration Release \
    --no-restore \
    --output /app/probe

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=server-build /app/publish/ ./
COPY --from=server-build /app/probe/ /app/probe/
USER $APP_UID
ENTRYPOINT ["dotnet", "Workbench.Web.dll"]
```

Before committing, enforce the digest requirement mechanically:

```powershell
$fromLines = @(Select-String -LiteralPath Dockerfile -Pattern '^FROM ' | ForEach-Object Line)
$unpinned = @($fromLines | Where-Object { $_ -notmatch '@sha256:[0-9a-f]{64}(\s+AS\s+\S+)?$' })
if ($unpinned.Count -ne 0) { throw "Unpinned Docker base: $($unpinned -join ', ')" }
```

`.dockerignore` must exclude `.git`, `.codex`, `**/bin`, `**/obj`, `**/node_modules`,
`**/dist`, `artifacts`, and local secret/config override files.

- [ ] **Step 3: Define the initial Compose topology**

Create `compose.yaml`:

```yaml
services:
  workbench:
    build:
      context: .
      dockerfile: Dockerfile
    image: workbench:local
    environment:
      ASPNETCORE_ENVIRONMENT: ContainerVerification
    ports:
      - "127.0.0.1:8080:8080"
    read_only: true
    tmpfs:
      - /tmp:size=64m,noexec,nosuid
    security_opt:
      - no-new-privileges:true
    cap_drop:
      - ALL
    healthcheck:
      test: ["CMD", "dotnet", "/app/probe/Workbench.HealthProbe.dll", "http://127.0.0.1:8080/health/ready"]
      interval: 5s
      timeout: 4s
      retries: 12
      start_period: 5s
    restart: unless-stopped
```

This Compose file is only a loopback-bound local verification topology. Its distinct
`ContainerVerification` environment exercises non-development middleware without claiming that the
phase satisfies production configuration validation. Do not expose it on a LAN/WAN or describe it
as production-ready. The later self-hosted deployment plan must add the accepted TLS reverse proxy,
private origin network, trusted-proxy list, allowed host, canonical public origin, and fail-closed
configuration validation. Do not mount source, Docker socket, secrets, or writable application
directories.

- [ ] **Step 4: Build and exercise the real container**

Run:

```powershell
docker compose build --pull
if ($LASTEXITCODE -ne 0) { throw 'Container build failed' }

try {
    docker compose up --detach --wait --wait-timeout 60
    if ($LASTEXITCODE -ne 0) { throw 'Container did not become healthy' }

    Invoke-RestMethod http://localhost:8080/health/live
    Invoke-RestMethod http://localhost:8080/health/ready
    Invoke-RestMethod http://localhost:8080/api/system/status
    $html = Invoke-WebRequest http://localhost:8080/
    if ($html.Content -notmatch '<div id="root"></div>') { throw 'React shell was not served' }

    $containerId = docker compose ps --quiet workbench
    if ([string]::IsNullOrWhiteSpace($containerId)) { throw 'Workbench container was not found' }
    $health = docker inspect $containerId --format '{{.State.Health.Status}}'
    if ($health -ne 'healthy') { throw "Unexpected container health: $health" }
    $runtime = docker inspect $containerId --format '{{.Config.User}} {{.HostConfig.ReadonlyRootfs}} {{json .HostConfig.CapDrop}}'
    if ($runtime -notmatch '^\d+ true \["ALL"\]$') { throw "Unexpected runtime security settings: $runtime" }
}
finally {
    docker compose down
}
```

Expected: Compose reports the service healthy; each request and the HTML assertion succeeds; inspect
reports a non-root numeric user, a read-only root filesystem, and all Linux capabilities dropped.

- [ ] **Step 5: Stop the test topology and commit**

```powershell
git add .dockerignore Dockerfile compose.yaml src/Workbench.HealthProbe Workbench.slnx
git commit -m "Package Workbench as a hardened container"
```

The Step 4 `finally` block removes containers and the network on success or failure while preserving
the built image for reuse.

---

### Task 5: Add repeatable local verification and development startup

**Files:**
- Create: `scripts/verify.ps1`
- Create: `scripts/dev.ps1`
- Modify: `README.md`
- Modify: `CONTRIBUTING.md`

**Interfaces:**
- Consumes: solution, client scripts, and container contract from Tasks 1–4.
- Produces: `pwsh -File scripts/verify.ps1` as the canonical local gate and
  `pwsh -File scripts/dev.ps1` as the local two-process development entrypoint.

- [ ] **Step 1: Write the verification script**

Create `scripts/verify.ps1`:

```powershell
[CmdletBinding()]
param(
    [switch] $IncludeContainer
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $fromLines = @(Select-String -LiteralPath Dockerfile -Pattern '^FROM ' | ForEach-Object Line)
    $unpinned = @($fromLines | Where-Object { $_ -notmatch '@sha256:[0-9a-f]{64}(\s+AS\s+\S+)?$' })
    if ($unpinned.Count -ne 0) { throw "Unpinned Docker base: $($unpinned -join ', ')" }

    dotnet restore Workbench.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

    dotnet build Workbench.slnx --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

    dotnet test Workbench.slnx --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }

    dotnet format Workbench.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet format failed' }

    npm --prefix src/Workbench.Client ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }

    npm --prefix src/Workbench.Client run check
    if ($LASTEXITCODE -ne 0) { throw 'client check failed' }

    if ($IncludeContainer) {
        docker compose build
        if ($LASTEXITCODE -ne 0) { throw 'container build failed' }
    }
}
finally {
    Pop-Location
}
```

The repository-wide `RestorePackagesWithLockFile` property from Task 1 generates both NuGet lock
files; `--locked-mode` must succeed before this step passes.

- [ ] **Step 2: Write the development coordinator**

Create `scripts/dev.ps1` so it:

1. validates `dotnet`, Node 24, and npm are available;
2. starts `dotnet watch --project src/Workbench.Web --urls http://localhost:5000`;
3. starts `npm --prefix src/Workbench.Client run dev`;
4. forwards Ctrl+C to both child processes; and
5. returns non-zero if either child exits unexpectedly.

Use `Start-Process -PassThru -NoNewWindow`, keep the two returned process objects in task-specific
variables, and stop only those exact process IDs in `finally`. Do not kill processes by executable
name.

- [ ] **Step 3: Document the runnable workflow**

Update `README.md` and `CONTRIBUTING.md` with exact prerequisites and commands:

```powershell
pwsh -File scripts/dev.ps1
pwsh -File scripts/verify.ps1
pwsh -File scripts/verify.ps1 -IncludeContainer
```

Document URLs `http://localhost:5173` for Vite development and `http://localhost:8080` for Compose.
State that Docker is required only for container verification in this phase and that the current
host did not have Docker available when the plan was authored.

- [ ] **Step 4: Run the canonical gate**

Run:

```powershell
pwsh -NoProfile -File scripts/verify.ps1
```

Expected: exit `0` after server restore/build/test/format and client clean install/check.

Run `-IncludeContainer` only where the Step 1 Docker precondition from Task 4 passes.

- [ ] **Step 5: Commit scripts and developer documentation**

```powershell
git add scripts README.md CONTRIBUTING.md src/Workbench.Web/packages.lock.json src/Workbench.HealthProbe/packages.lock.json tests/Workbench.Web.IntegrationTests/packages.lock.json
git commit -m "Document and automate local verification"
```

---

### Task 6: Enforce the foundation in CI and CodeQL

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `.github/dependabot.yml`
- Modify: `.github/workflows/codeql.yml`

**Interfaces:**
- Consumes: `scripts/verify.ps1`, Dockerfile, Compose health endpoints, package locks.
- Produces: required CI jobs `application`, `container`, and expanded CodeQL analysis for `actions`,
  `csharp`, and `javascript-typescript`.

- [ ] **Step 1: Add the application CI job**

Create `.github/workflows/ci.yml` with least-privilege `contents: read`, concurrency cancellation by
workflow/ref, and this application sequence on `ubuntu-latest`:

```yaml
- uses: actions/checkout@v7
- uses: actions/setup-dotnet@v5
  with:
    dotnet-version: 10.0.x
- uses: actions/setup-node@v5
  with:
    node-version: 24.20.0
    cache: npm
    cache-dependency-path: src/Workbench.Client/package-lock.json
- name: Verify application
  shell: pwsh
  run: ./scripts/verify.ps1
- name: Enforce .NET vulnerability policy
  run: dotnet restore Workbench.slnx --locked-mode
- name: Check production npm packages
  run: npm --prefix src/Workbench.Client audit --omit=dev --audit-level=high
```

Resolve each action tag to its current immutable commit with `gh api`, review that commit in the
action's upstream repository, then replace the tags above with the returned 40-character SHA while
retaining the readable version in an adjacent comment:

```powershell
gh api repos/actions/checkout/commits/v7 --jq .sha
gh api repos/actions/setup-dotnet/commits/v5 --jq .sha
gh api repos/actions/setup-node/commits/v5 --jq .sha
```

Do not merge a workflow that still references a mutable tag.

`Directory.Build.props` sets `NuGetAuditMode=all`, `NuGetAuditLevel=moderate`, and promotes
`NU1901`–`NU1904` to errors. Therefore the locked restore deterministically fails for any direct or
transitive package at or above the accepted moderate threshold; a listing-only command is not the
security gate.

- [ ] **Step 2: Add the container CI job**

Add a job that depends on `application`, builds the Dockerfile, runs the image on port `8080`, waits
up to 60 seconds for `/health/ready`, then checks `/api/system/status` and `/`. Capture container logs
on failure and always remove the container.

The smoke loop must fail after its deadline; it must not hide a failed request with an unconditional
success command.

After the smoke test, scan the exact locally built image and fail for fixed or unfixed `HIGH` or
`CRITICAL` operating-system or application-library vulnerabilities:

```yaml
- name: Scan container image
  uses: aquasecurity/trivy-action@v0.36.0
  with:
    image-ref: workbench:ci
    format: table
    exit-code: '1'
    ignore-unfixed: false
    vuln-type: os,library
    severity: HIGH,CRITICAL
```

Replace the Trivy tag with the reviewed immutable SHA using the same process as every other action.
Do not add an ignore file or suppress an advisory without a documented review and explicit approval.

- [ ] **Step 3: Expand CodeQL without weakening the existing Actions scan**

Change the CodeQL matrix to:

```yaml
matrix:
  include:
    - language: actions
      build-mode: none
    - language: csharp
      build-mode: manual
    - language: javascript-typescript
      build-mode: none
```

Replace the existing manual-build step with a C#-only step that installs .NET 10 and runs:

```powershell
dotnet restore Workbench.slnx --locked-mode
dotnet build Workbench.slnx --configuration Release --no-restore
```

Keep the existing minimal permissions and scheduled scan.

Pin every `uses:` reference in both `.github/workflows/ci.yml` and the modified CodeQL workflow,
including `actions/checkout`, `actions/setup-dotnet`, `actions/setup-node`,
`aquasecurity/trivy-action`, and both `github/codeql-action/init` and
`github/codeql-action/analyze`. Resolve the additional upstream tags with:

```powershell
gh api repos/aquasecurity/trivy-action/commits/v0.36.0 --jq .sha
gh api repos/github/codeql-action/commits/v4 --jq .sha
```

- [ ] **Step 4: Assign recurring dependency and digest updates**

Create `.github/dependabot.yml` so pinned Docker digests, GitHub Action SHAs, npm packages, and NuGet
packages receive reviewable weekly pull requests:

```yaml
version: 2
updates:
  - package-ecosystem: docker
    directory: /
    schedule:
      interval: weekly
  - package-ecosystem: github-actions
    directory: /
    schedule:
      interval: weekly
  - package-ecosystem: npm
    directory: /src/Workbench.Client
    schedule:
      interval: weekly
  - package-ecosystem: nuget
    directories:
      - /src/Workbench.Web
      - /src/Workbench.HealthProbe
      - /tests/Workbench.Web.IntegrationTests
    schedule:
      interval: weekly
```

Do not auto-merge dependency updates. Review the upstream release and changed digest/SHA, regenerate
locks where applicable, and require the full verification, image scan, and CodeQL gates.

- [ ] **Step 5: Validate workflow syntax and run all local equivalents**

Run:

```powershell
pwsh -NoProfile -File scripts/verify.ps1
pwsh -NoProfile -File scripts/verify.ps1 -IncludeContainer
$workflowFiles = @('.github/workflows/ci.yml', '.github/workflows/codeql.yml')
$mutableActions = Select-String -Path $workflowFiles -Pattern 'uses:\s+\S+@(?![0-9a-f]{40}\s*(?:#.*)?$)'
if ($mutableActions) { throw "Mutable action references remain: $($mutableActions -join ', ')" }
git diff --check
```

Expected: all commands exit `0`. Inspect the Actions run after push and require all three CodeQL
matrix entries plus both CI jobs to complete successfully before merging.

- [ ] **Step 6: Commit CI enforcement**

```powershell
git add .github/workflows/ci.yml .github/workflows/codeql.yml .github/dependabot.yml
git commit -m "Enforce application foundation checks"
```

---

## Final Phase Verification

- [ ] Run `pwsh -NoProfile -File scripts/verify.ps1` and record its zero exit code.
- [ ] Run `pwsh -NoProfile -File scripts/verify.ps1 -IncludeContainer` on a Docker-capable host.
- [ ] Run `git diff --check` and confirm no unstaged or untracked implementation files remain.
- [ ] Confirm the published/containerized root page is the React shell and `/api/not-a-route` is
  `404`, not `index.html`.
- [ ] Confirm the container runs non-root, has a read-only root filesystem, and has no added Linux
  capabilities.
- [ ] Confirm Compose reports the container healthy through the packaged readiness probe.
- [ ] Confirm the container vulnerability scan has no unresolved `HIGH` or `CRITICAL` result.
- [ ] Confirm every GitHub Actions `uses:` reference is pinned to a reviewed 40-character commit SHA.
- [ ] Confirm CI and all three CodeQL language entries pass on the implementation branch.
- [ ] Compare the delivered phase against the accepted spec sections for system architecture,
  deployment release unit, stateless web tier, health behavior, verification, and cost controls.
- [ ] Run a Codex Security diff scan over the exact implementation branch versus its base, record the
  scan ID and report, and resolve every reportable finding before merge. A source-level scan is
  required here because the implementation phase introduces executable code and workflows.
- [ ] Write the next plan, `2026-08-31-platform-data-and-identity.md`, from the now-real project
  structure before introducing SQL or authentication code.
