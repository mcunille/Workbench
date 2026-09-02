# SQL Persistence, Tenant Isolation, and Built-in Identity

**Status:** Implemented

## Summary

This phase introduces SQL Server as Workbench's authoritative structured-data store, enforces
shared-database tenant isolation, and delivers built-in password authentication with durable
server-side sessions. The design follows a zero-trust model: the browser, network location,
authentication cookie, application query, and database connection are each insufficient on their
own to establish tenant authority.

Tenant isolation is enforced in complementary layers. Workbench derives a user's one tenant from a
durable session, establishes one immutable tenant context for the request, filters and validates
tenant data in Entity Framework Core, enforces tenant-consistent relationships, and applies SQL
Server row-level security (RLS) to tenant-owned tables. A missing or mismatched context fails closed.

Built-in credentials are one identity-verification method behind a provider-neutral boundary.
Workbench remains authoritative for users, tenants, roles, permissions, account status, sessions,
and audit history so a later external OpenID Connect verifier does not change domain authorization.

This specification implements GitHub issue #10 and the data-and-identity phase of the accepted base
architecture. Inventory, purchasing, ledger behavior, document schemas, blob providers, SMTP,
public recovery/invitation provider abuse controls, and external OIDC configuration remain separate work.

## Context and implementation evidence

The application is a .NET 10 ASP.NET Core API and React/TypeScript client that publish as one
same-origin release unit. The implementation now includes SQL persistence, authentication, immutable
tenant context, a durable protected data-protection key ring, explicit migration operations, and
authoritative SQL readiness.

The accepted architecture already requires SQL Server/Azure SQL, server-derived tenancy, exactly
one tenant per user, built-in ASP.NET Core Identity, durable session validation, explicit migrations,
separate web and migration database permissions, and invalidation of restored authentication state.
This phase selects the exact implementation and verification strategy for those requirements.

## Goals

- Establish one versioned SQL Server/Azure SQL schema and explicit migration operation.
- Enforce tenant isolation for reads, writes, relationships, identities, and administration.
- Provide built-in password sign-in, sign-out, password change, invitation, and recovery semantics.
- Check authoritative server-side session and account state on every protected request.
- Keep domain authorization independent from the identity verifier.
- Provide one-time operator bootstrap and tenant-provisioning commands with no public registration.
- Separate web, migration, and operator database authority and verify the separation mechanically.
- Make concurrency, audit, backup, restore, and authentication invalidation explicit.
- Preserve the same-origin, stateless, multi-replica release model.
- Complete adversarial review and a security diff scan with no unresolved reportable findings.

## Non-goals

- Inventory, purchasing, accounting, commerce, attachments, or other product-domain schemas.
- A detailed product role matrix beyond tenant administrator and tenant member.
- A web-facing platform-administration console or cross-tenant browsing.
- SMTP delivery, public recovery/invitation abuse-control integration, or production background
  delivery workers. Shared SQL login limiting is in scope.
- External OIDC provider configuration or account-linking user interfaces.
- Production service-level, recovery-point, or recovery-time commitments.
- Automatic production database restore, cutover, or destructive rollback.

## Zero-trust security invariants

1. Network location never grants identity, tenant, operator, or migration authority.
2. A protected cookie carries only a protected random session reference and format version. It is
   not authoritative user, tenant, role, or permission state.
3. Every protected request loads and validates the durable session, user, tenant, security version,
   expiration, and revocation state before constructing its request actor.
4. `TenantId` is derived from durable authenticated state. Routes, request bodies, query strings,
   headers, cookies, and client-generated claims cannot select or override it.
5. A tenant-scoped data context cannot be constructed without an immutable tenant context. Missing
   tenant context also produces no rows and blocks writes at the SQL RLS boundary.
6. Every tenant-owned row carries a non-null `TenantId`; tenant-local uniqueness and relationships
   include it.
7. Application authorization uses named permissions and denies by default. Domain code does not
   authorize from provider claims or hard-coded identity-provider role names.
8. The web, migration, and operator database principals are distinct. The web process never
   receives migration or operator credentials.
9. Privileged work uses explicit narrow commands or procedures and append-only audit records. There
   is no general application-level RLS bypass or implicit platform-administrator tenant access.
10. Passwords, raw session identifiers, raw recovery or invitation tokens, connection strings,
    bootstrap secrets, and data-protection key material never enter logs, audit metadata, API
    responses, browser storage, source control, or container images.
11. Production startup and readiness fail closed when identity, SQL, key protection, schema, RLS,
    or database permission configuration is missing or unsafe.
12. Security behavior is proven against a real SQL Server provider, including adversarial raw SQL
    and pooled-connection reuse; EF InMemory is not acceptable evidence.

## Component boundaries

The application remains one modular monolith. The server gains focused boundaries rather than new
deployable services:

- **Persistence** owns the EF Core model, SQL connection configuration, migrations, interceptors,
  database health, and schema compatibility checks.
- **Tenancy** owns immutable tenant context, tenant lifecycle, RLS session-context establishment,
  and tenant ownership conventions.
- **Identity** owns built-in credential verification, provider-neutral verified identities,
  accounts, invitations, recovery operations, sessions, and data-protection key persistence.
- **Authorization** resolves the current request actor and named tenant permissions.
- **Security audit** appends allowlisted tenant or system security events without exposing a
  cross-tenant query interface.
- **Administration** exposes tenant-local user administration through authenticated APIs and
  platform provisioning through an operator-only command surface.
- **Database migrator** is a separate executable invoked explicitly with a migration principal. It
  is never called by web startup.

HTTP contracts use request and response records rather than database entities. Domain modules added
later consume an `IRequestActor` containing the authoritative local user ID, tenant ID, session ID,
and resolved permission set. They do not consume ASP.NET Identity users, external-provider claims,
or caller-supplied tenant identifiers.

## Authoritative schema

### Identifier and concurrency conventions

Application code creates GUID identifiers. Consequential mutable records use SQL Server `rowversion`
for optimistic concurrency. UTC timestamps are generated or validated server-side. Public update
contracts carry opaque concurrency values and conflicts return a stable conflict response rather
than silently overwrite state.

Every tenant-owned table has:

- a non-null `TenantId`;
- a primary key on `Id` and a unique alternate key on `(TenantId, Id)`;
- tenant-scoped unique indexes where the value is tenant-local;
- composite foreign keys that carry `TenantId`; and
- SQL RLS filter and block predicates.

The first migration creates these records:

| Record | Purpose and important constraints |
| --- | --- |
| `Tenants` | Tenant identity, normalized name, enabled state, timestamps, and `rowversion`. Tenant names are unique only where product behavior requires it; names are not authority. |
| `Users` | ASP.NET Core Identity user extended with non-null `TenantId`, account state, security version, timestamps, and `rowversion`. `(TenantId, Id)` references `Tenants`. |
| `Roles` | Tenant-local Identity role with non-null `TenantId`; normalized role name is unique within the tenant. |
| `UserRoles` | Tenant-consistent membership whose composite foreign keys require user and role to share `TenantId`. |
| Identity support tables | Claims, logins, and tokens carry tenant-consistent ownership where used. Provider login rows preserve issuer/scheme plus provider subject without becoming domain authority. |
| `LoginDirectory` | A global security index from globally unique normalized email to user and tenant IDs. The web principal has no direct read access; an exact-match login procedure is its only unauthenticated lookup. |
| `Sessions` | SHA-256 hash of a random 256-bit session token, user and tenant IDs, account security version, created/last-seen/idle/absolute expiry, revocation state and reason, safe client metadata, and `rowversion`. The raw token exists only inside the protected cookie. |
| `IdentityOperations` | Hashed, single-use invitation or recovery token, purpose, tenant/user, issue security version, expiry, consumption/revocation state, and `rowversion`. |
| `TenantSecurityAuditEvents` | Append-only tenant, actor, action, target, outcome, correlation ID, timestamp, and allowlisted versioned metadata. |
| `SystemSecurityAuditEvents` | Append-only bootstrap, provisioning, migration, restore, and database-security events. It exposes no tenant-data browsing path. |
| `DatabaseSecurityState` | Singleton schema/security generation, restore generation, last completed restore sanitization, and compatibility metadata. |
| Data-protection key table | Shared ASP.NET Core Data Protection key ring protected at rest by deployment-supplied key material. |

Normalized email is globally unique. This is necessary because a user belongs to exactly one tenant
and login does not ask the browser to select a tenant. The exact-match login procedure returns only
the account data needed to verify one submitted normalized email. It executes under a narrowly
defined database context; the web principal cannot enumerate the login directory or use that
context for arbitrary tenant access.

### Tenant context lifecycle

Unauthenticated requests have no tenant context. Login uses only the exact-match credential lookup.
After password verification, the server creates a durable session bound to the user's current tenant
and security version.

For a protected request:

1. Cookie authentication unprotects the session reference.
2. Session validation loads the session and current account/tenant state.
3. Any missing, revoked, expired, disabled, or security-version-mismatched state rejects the cookie.
4. Successful validation creates one immutable request actor and tenant context.
5. Tenant-scoped application and EF services may then be resolved and used.
6. A SQL connection interceptor writes the tenant ID into `SESSION_CONTEXT` with `@read_only = 1`
   every time a logical connection opens. No command may run first. Returning the logical connection
   to the pool resets that context; a subsequent checkout must establish a fresh read-only value
   before tenant SQL is issued. The application never changes tenants on an open connection.
7. EF query filters add the same tenant predicate. A save interceptor assigns `TenantId` for new
   rows and rejects missing, mismatched, or modified ownership.
8. SQL RLS independently filters reads and blocks inserts or updates whose `TenantId` does not match
   `SESSION_CONTEXT`. A null context matches no tenant.

The web principal cannot disable security policies or replace the read-only tenant context on an
open connection. Tests use `IgnoreQueryFilters` and raw SQL to prove the RLS boundary remains
effective. Tests also alternate tenants over a deliberately small connection pool and inject
failed/cancelled requests to prove stale context does not leak.

Operator commands and migrations do not reuse the tenant web data context. They use separate
interfaces, principals, procedures, audit rules, and connection pools. Background work added later
must establish an explicit tenant from its durable work record before resolving tenant services.

## Identity verification and provider independence

Built-in passwords are implemented with ASP.NET Core Identity's maintained password hasher and
policy mechanisms. Workbench wraps password verification behind an identity-verifier contract whose
result identifies a verifier scheme and stable verifier subject. A local account resolver maps that
verified identity to one Workbench user. Only the local user record supplies tenant, account state,
roles, permissions, session authority, and audit identity.

A later OIDC verifier may produce the same provider-neutral verified identity. External identity
linking must be an explicit verified operation and defend against issuer/subject confusion and
unintended account merging. Adding it must not change `IRequestActor`, tenant resolution, session
validation, or domain authorization policies.

The initial named permissions are deliberately small:

- `TenantAccess` for an enabled authenticated tenant member; and
- `TenantUsersManage` for a tenant administrator managing users and sessions in the same tenant.

`TenantAdministrator` and `TenantMember` roles grant these permissions. APIs authorize named
permissions, not direct role string comparisons. Platform provisioning is not a tenant permission.

## Built-in authentication flows

### Antiforgery and cookie policy

The same-origin client obtains an antiforgery request token from a dedicated endpoint and sends it
in a request header for every state-changing browser request, including login, sign-out, password
change, recovery consumption, and administration. Authentication and antiforgery cookies are
`Secure` outside explicit local development, `HttpOnly` where browser script need not read them,
and at least `SameSite=Lax`. Known API endpoints return `401` or `403`, never HTML redirects.

HTTPS, canonical public origin, durable protected data-protection keys, and safe proxy/host settings
are mandatory outside local development. Verification and recovery links are built only from the
configured canonical public origin, never request `Host` or untrusted forwarded headers.

### Sign-in and durable sessions

Sign-in accepts normalized email and password but never tenant input. Success creates a random
256-bit session token, stores only its SHA-256 hash, and places the raw token only inside the
protected opaque cookie. Failure uses a common response for missing, disabled, or incorrect accounts
and records only safe audit information.

Every protected request reads and validates the durable session. Default session limits are a
30-minute idle timeout and 12-hour absolute lifetime. There is no remember-me mode in this phase.
Both limits are configurable only within validated security bounds. `LastSeen` persistence is
throttled to reduce write amplification, but session validity is still read and checked on every
request. Revoking a session is effective across replicas without waiting for a validation interval.

Sign-out revokes the current durable session before deleting the cookie. Users may list safe details
for their own sessions and revoke one or all of them. Raw session references are never returned.

### Password and account security changes

Password change verifies the current password, writes the new framework-generated password hash,
advances the account security version, revokes all sessions, and appends the audit event in one
transaction. Recovery, account disablement, tenant-authority changes, and administrative password
reset follow the same security-version and revocation invariant. A user signs in again after a
successful password change.

### Invitation and recovery operations

Invitation and recovery use cryptographically random, short-lived, single-purpose tokens. SQL stores
only a token hash plus operation ID, purpose, tenant/user, issue security version, expiry,
consumption/revocation state, and audit metadata. The raw token exists only in the delivery message.

A recovery request always returns the same `202 Accepted` contract whether the account exists, is
disabled, or is unknown. For an eligible account, it creates an operation and passes a canonical
link to `IIdentityMessageDelivery`. The API never returns or logs the raw link. Consumption locks the
operation and, in one transaction, verifies purpose, token hash, expiry, unused state, and current
security version; changes the password; advances security state; revokes all sessions; consumes the
operation; and appends the audit event. Exactly one concurrent consumption can succeed.

Tenant administrators may invite and manage only users whose `TenantId` equals their request tenant.
Invitations use the same single-use machinery and provider-neutral delivery boundary. The production
delivery and request endpoints remain startup-disabled until issue #11 supplies a real message
provider and their complete shared abuse controls. Login already uses normalized-account and
trusted-network partitions in a multi-replica-safe SQL limiter. Integration tests use an in-memory
capture adapter. Local development may explicitly enable a capture adapter and retrieve a link through a
development CLI command; the browser never receives the link from the request endpoint.

## Administration and bootstrap

There is no public registration and no web-facing platform-administration interface.

The first tenant and administrator are created by an explicit one-time bootstrap command. The
command receives the tenant name and administrator credentials through protected environment/secret
input, executes after migrations, creates the tenant/account/roles/audit record transactionally, and
refuses to run after initialization. Normal web startup neither reads bootstrap credentials nor
seeds accounts.

Additional tenants are created through an operator-only CLI command. It uses a separate operator
principal and a narrow audited procedure or application command; it cannot perform arbitrary DDL or
browse tenant data. Tenant administrators manage users only inside their own tenant.

Local and agent development uses a committed `.env.dev.example` and gitignored `.env.dev` containing
local SQL and stable development-admin settings. Development scripts prefer the current worktree's
file and may use an explicit `WORKBENCH_DEV_ENV_FILE` path for a shared file. Scripts consume secrets
without printing them. `AGENTS.md` permits agents to use the file for development while forbidding
logging, returning, or committing its values. The development bootstrap uses those credentials only
when the local database is empty; subsequent agent sessions reuse them for login. Integration tests
create isolated tenants and users and never depend on development credentials.

Production bootstrap credentials are one-time secrets and are removed after success. They are not
the migration credential and are never made available to the web process.

## Database principals and migration security

The database defines three distinct authority classes:

- **Web principal:** required runtime DML and narrowly named procedure execution only. It cannot
  alter schema, RLS, grants, users, procedures, or migration history.
- **Migration principal:** only the database-level DDL, RLS-policy, migration-history, and data
  transition permissions required by versioned migrations. It has no server administration or
  authority over unrelated databases.
- **Operator principal:** only named bootstrap, tenant-provisioning, restore-sanitization, and
  verification operations. It cannot browse arbitrary tenant data or perform arbitrary DDL.

These principals cannot impersonate one another. Permission tests connect as the actual principals
and demonstrate both allowed and denied operations.

Hosted Azure deployments use separate managed identities for the web application and migration job.
The migration job obtains a short-lived token and runs from the exact application image digest being
released. The web runtime is never assigned the migration identity or configuration.

Self-hosted deployments provide the migration credential through a mounted secret file or protected
interactive input. It is absent from `.env.dev`, production environment variables, Compose service
configuration, image layers, and web application configuration. The secret is available only for
the migrator process lifetime and can be rotated or disabled after deployment.

Migration execution verifies target database identity, current schema version, expected release
version, and artifact hash. It acquires a database application lock so only one migrator runs. It
records safe provenance and outcome without secrets. Web startup never applies migrations.

## Migration and release strategy

EF Core migrations live with a separate database migrator executable. SQL objects that EF cannot
model directly—RLS functions and policies, exact-match procedures, roles, and grants—are created by
reviewed idempotent migration SQL embedded in the migration. Readiness requires a schema version
inside the release's declared compatibility range and verifies the expected security objects remain
enabled.

Migration verification runs against a pinned real SQL Server container and covers:

1. an empty database to the current schema;
2. the previous supported release schema to current;
3. upgrade with representative two-tenant identity, session, recovery, and audit data;
4. a reversible down migration in a disposable database;
5. restoration from a backup when schema rollback is incompatible;
6. migration locking and repeat invocation;
7. migration-principal success;
8. web-principal denial of DDL, security-policy changes, and migration-history writes; and
9. post-migration schema, constraint, RLS, grant, and readiness probes.

Application rollback may deploy a prior image only while the schema is inside that image's declared
compatibility window. Destructive changes require expand, migrate, and contract releases plus a
verified backup. Production data rollback uses the documented human-operated restore procedure; no
script silently replaces a production database.

## Audit, concurrency, and failure behavior

Security and administration mutations use explicit transactions. Session revocation,
security-version changes, password changes, and recovery consumption are atomic. Identity operations
are locked during consumption, and `rowversion` detects competing administrative updates. Stable
Problem Details distinguish unauthenticated, unauthorized, validation, conflict, unavailable, and
unexpected failures without exposing account existence, tenant existence, SQL details, or security
state.

Tenant security audit events capture tenant, actor, action, target type/ID, timestamp, correlation
ID, outcome, and small allowlisted versioned metadata. System events capture bootstrap,
provisioning, migration, and restore actions. The web principal can append allowed audit events but
cannot update or delete audit history. Credentials, tokens, session references, password material,
connection strings, request bodies, and unnecessary personal data are prohibited metadata.

Tenant-boundary violations fail closed and emit rate-controlled security telemetry containing safe
correlation and local identifiers without returning evidence about another tenant.

## Readiness

Readiness, but not liveness, requires:

- reachable SQL through the web principal;
- a compatible migration version;
- enabled expected RLS policies and security objects;
- a protected tenant-context proof key that makes the web database credential alone insufficient;
- successful least-privilege permission probes;
- accessible durable data-protection key storage and safe at-rest protection;
- no independent restore-pending marker and completed restore sanitization state; and
- valid environment-specific identity, cookie, HTTPS, canonical-origin, and proxy configuration.

Expensive checks may be cached briefly, but any discovered mismatch removes readiness. A replica
does not serve protected traffic in an assumed-safe degraded mode.

## Backup and restore

SQL backup contains tenant data, password hashes, login directory entries, audit history, durable
sessions, invitation/recovery operations, database security state, and protected data-protection
keys. Backup media is therefore a credential-bearing security asset and requires encryption, access
control, retention, and tested restoration.

Hosted guidance uses Azure SQL point-in-time recovery and platform backup controls. Self-hosting
receives operator-run SQL backup and restore scripts. Backup and restore remain human-operated.

The restore script writes an independent pending marker into the restored database before returning
it to multi-user mode. After any restore, traffic stays stopped while a mandatory post-restore command:

1. verifies database identity and migration compatibility;
2. deletes all restored sessions, invitations, and recovery operations;
3. advances global restore/security generation;
4. replaces the restored data-protection key ring;
5. reapplies or verifies RLS policies and grants;
6. records a post-restore system audit event;
7. runs tenant-isolation and principal-permission probes; and
8. marks restore sanitization complete for readiness.

This prevents restored cookies, invitations, and reset links from becoming valid. The runbook also
requires reconciliation because account disabling, password changes, tenant administration, and
audit events after the backup point may have been rolled back. Exact recovery objectives remain a
deployment commitment rather than an application constant.

## Client behavior

The same-origin React client adds accessible sign-in, recovery request/reset, authenticated
bootstrap, sign-out, session management, and bounded tenant-user administration screens. It never
stores access or refresh tokens. It obtains an antiforgery token and sends it on state-changing
requests. Authentication bootstrap distinguishes unauthenticated, forbidden, temporarily
unavailable, and unexpected states without mounting protected content optimistically.

Generated OpenAPI TypeScript remains the API contract boundary. Client tests intercept HTTP rather
than mock generated internals. User-facing authentication errors are stable and non-enumerating.

## Test-first implementation breakdown

Implementation follows red-green-refactor. No production behavior is added before a focused test has
failed for the expected missing behavior.

1. **SQL test harness and migrator seam**
   - Add a pinned real SQL Server integration fixture and a failing clean-migration test.
   - Run the focused integration test and confirm failure because no migrator/schema exists.
   - Add the minimal migrator, schema-version check, and test principal creation.
   - Re-run focused and existing server tests.
2. **Tenant schema, context, and RLS**
   - Add failing tests for null context, cross-tenant reads/writes/relationships, raw SQL,
     `IgnoreQueryFilters`, and pooled connection reuse.
   - Add tenant entities, composite constraints, immutable context, EF filters/interceptors, and RLS.
   - Re-run isolation tests, then all SQL integration tests.
3. **Identity accounts and exact login lookup**
   - Add failing password-login, generic-failure, no-tenant-input, and permission-denial tests.
   - Add Identity schema, login directory/procedure, verifier boundary, and named permissions.
   - Re-run authentication and cross-tenant suites.
4. **Durable sessions and data protection**
   - Add failing tests for opaque cookies, per-request SQL validation, idle/absolute expiration,
     account disablement, security-version mismatch, single/all-session revocation, and replicas.
   - Add durable session validation and protected SQL key ring.
   - Re-run authentication, pooling, and published same-origin tests.
5. **Antiforgery and browser authentication**
   - Add failing API and client tests for login CSRF, bootstrap, sign-out, and stable failures.
   - Add antiforgery contracts and React flows.
   - Re-run server, client, and browser authentication tests.
6. **Invitation, recovery, and tenant administration**
   - Add failing non-enumeration, hash-at-rest, expiry, reuse, race, session-revocation,
     cross-tenant administration, and disabled-production-provider tests.
   - Add identity operations, delivery boundary/capture adapter, and bounded administration APIs/UI.
   - Re-run identity, SQL concurrency, client, and browser tests.
7. **Bootstrap, provisioning, and principal separation**
   - Add failing one-time bootstrap, repeat refusal, additional-tenant provisioning, audit, and
     actual-principal permission tests.
   - Add operator CLI commands/procedures, `.env.dev` workflow, and least-privilege grants.
   - Re-run operator, RLS, migration, and development-script tests.
8. **Readiness, migration rollback, backup, and restore sanitization**
   - Add failing readiness-version/RLS/grant tests and restore-revival tests.
   - Add migration verification, backup/restore guidance/scripts, post-restore sanitization, and
     readiness checks.
   - Run clean, upgrade, disposable downgrade, incompatible restore, and full verification drills.
9. **Documentation and security closure**
   - Update living architecture, README, contributor, operations, and security documentation.
   - Run an acceptance-criteria review, focused threat-model review, and security diff scan.
   - Fix every reportable finding and repeat the affected tests and scan.
   - Run the complete clean verification and container smoke gates.

The temporary implementation plan supplies exact file paths, test names, and commands before code
changes begin. It is not committed, in accordance with repository guidance.

## Executable migration and rollback verification

The implementation must make these repository-level operations available through PowerShell scripts
with locked dependencies and nonzero exit codes on any skipped or failed gate:

```powershell
# Start or reuse the pinned disposable SQL Server used by integration tests.
./scripts/test-sql.ps1 -Action Start

# Apply the current migration artifact to a clean database, then verify schema,
# RLS, grants, readiness compatibility, and repeat execution.
./scripts/verify-migrations.ps1 -Scenario Clean

# Restore the checked-in prior-schema fixture, load representative two-tenant
# data, migrate forward, and verify data plus isolation.
./scripts/verify-migrations.ps1 -Scenario Upgrade

# In a disposable database only, migrate down to the declared prior compatible
# schema and prove the prior application contract can run.
./scripts/verify-migrations.ps1 -Scenario ReversibleRollback

# Restore a disposable SQL backup, run mandatory sanitization, and prove every
# pre-restore session, invitation, recovery token, and data-protection key fails.
./scripts/verify-migrations.ps1 -Scenario RestoreRollback

# Connect as each actual database principal and verify the allow/deny matrix.
./scripts/verify-database-permissions.ps1
```

Names may be adjusted only if the accepted implementation plan records the exact replacement. The
scenarios and evidence may not be weakened or replaced with provider fakes.

## Verification matrix

| Property | Required evidence |
| --- | --- |
| Every tenant row is owned | Model/schema convention test plus SQL metadata inspection |
| Absent tenant fails closed | EF and raw SQL reads return no rows; writes are blocked |
| Cross-tenant access fails | Negative HTTP, EF, raw SQL, relationship, and identifier-substitution tests |
| Pooling is safe | Alternating-tenant and failed-request tests over a small reused connection pool |
| Client cannot choose tenant | Contract inspection plus body/route/header substitution tests |
| Session is authoritative | Per-request SQL validation, revocation, expiry, disablement, and replica tests |
| Identity provider is decoupled | Verifier-boundary unit tests and authorization tests using local identity only |
| Principals are separated | Actual web/migration/operator allow-and-deny integration tests |
| Recovery is safe | Non-enumeration, token hashing, expiry, race, reuse, and session-revocation tests |
| Restore invalidates auth | Disposable restore drill covering sessions, operations, and key ring |
| Concurrency is explicit | Competing account/session/operation mutation tests |
| Audit is append-only and safe | Permission, atomicity, and prohibited-metadata tests |
| Release remains portable | Published same-origin and hardened container smoke tests with SQL readiness |

## Security review

The focused threat model and final security diff review cover tenant-boundary bypass, insecure direct
object references, session theft and replay, login/recovery enumeration, credential stuffing
boundaries, CSRF, invitation/recovery races, unsafe cookie/key configuration, SQL injection,
connection-context leakage, RLS bypass, migration credential theft, principal escalation,
administrative bypass, backup disclosure, and restored-authentication revival.

Implementation is incomplete while a reportable security finding remains unresolved. A zero-finding
scan does not replace correctness review or the real-SQL adversarial suite.

## Alternatives considered

### Security views and stored procedures for all persistence

This can provide strong database enforcement but makes routine EF persistence, Identity integration,
and migrations procedure-centric. It adds substantial mapping and operational complexity before
domain behavior exists. Narrow procedures remain appropriate for tenantless login and operator work,
but not as the only persistence interface.

### EF query filters and constraints without RLS

Rejected. Query filters and composite keys are valuable safeguards, but a forgotten filter or raw
SQL query can still read another tenant's rows. They do not meet the accepted architecture's
database-enforced read-isolation requirement.

### Database per tenant

Rejected for the initial architecture. It changes provisioning, migrations, pooling, reporting,
backup, and operating cost substantially and conflicts with the accepted shared-database direction.
The application boundary still avoids unnecessary cross-tenant coupling should measured needs later
justify stronger physical isolation.

### Automatic account seeding during web startup

Rejected. It gives every replica bootstrap behavior, requires long-lived seed secrets in web
configuration, couples startup to privileged mutation, and complicates migration ordering. Explicit
one-time bootstrap is narrower and auditable.

### Stateless authorization cookie or bearer tokens in browser storage

Rejected. They cannot satisfy per-request durable revocation and increase authorization-staleness or
browser-token exposure. An opaque cookie plus authoritative SQL session provides immediate shared
revocation across replicas.

### Public setup or tenant-registration endpoint

Rejected. This phase has no product requirements for self-service tenancy and no reason to expose a
remotely reachable platform-provisioning surface. Operator-only CLI provisioning is explicit and
auditable.

## Compatibility and rollout

This phase changes readiness and adds a required SQL dependency. Local development, hosted, and
self-hosted profiles must provide separate web and migration identities plus durable key protection.
The React/API origin and one-image release remain unchanged. The database migrator may run from the
same verified image as a separate command, but migration credentials are supplied only to the
migration operation.

The first release has no application-data predecessor, so its initial migration is additive relative
to the foundation. Subsequent releases declare schema compatibility explicitly. Deployment order is
backup when required, migrate, verify security and permissions, deploy web replicas, pass readiness,
then admit traffic.

## Residual risks and deferred decisions

- SMTP delivery and complete shared recovery/invitation abuse controls are required before enabling
  public invitation or recovery in production; issue #11 owns those providers. Shared login limiting
  is implemented in this phase.
- External OIDC linking requires a separate security-reviewed design despite the verifier seam.
- Platform self-service tenant creation and cross-tenant support tooling require explicit authority,
  privacy, audit, and impersonation design.
- Exact session lifetime bounds may be tightened when real usage and risk evidence exists; defaults
  in this phase remain 30 minutes idle and 12 hours absolute.
- Database DDL authority is intrinsically powerful even when scoped. Deployment identity isolation,
  short-lived credentials, artifact provenance, and audit reduce but do not eliminate that risk.
- A database restore can revert account and audit changes after the backup point. Sanitization blocks
  restored authentication artifacts, but operators must still reconcile lost administrative state.

## Acceptance criteria

The phase is complete when:

1. this specification is accepted and living architecture reflects the implemented result;
2. SQL Server migrations and rollback/restore drills pass through the documented commands;
3. every tenant-owned table and relationship satisfies the tenant schema conventions;
4. missing and cross-tenant context fail closed through HTTP, EF, raw SQL, and RLS tests;
5. users belong to exactly one tenant and no client contract accepts tenant authority;
6. every protected request validates durable session and current account state;
7. credential, session, recovery, invitation, bootstrap, and administration flows satisfy this spec;
8. domain authorization depends on local request-actor permissions, not an identity provider;
9. actual web, migration, and operator principals pass the allow/deny permission matrix;
10. concurrency conflicts, append-only audit, and restore invalidation pass real-SQL tests;
11. source, client, browser, publish, migration, restore, and hardened-container verification pass;
12. the threat-model review and final security diff scan have no unresolved reportable findings; and
13. the issue #10 checklist is traceable to fresh verification evidence rather than design intent.
