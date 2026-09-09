# Per-worktree development environments

Status: Implemented and locally verified on Windows/Docker Desktop, 2026-09-09 UTC.

## Problem and scope

Each agent needs to run its current Workbench checkout independently and give the user a
working browser URL. Several previews must coexist, including authenticated sessions and
different database schemas. Closing an agent terminal must not stop the preview.

Today `scripts/dev-env.ps1` loads checkout-specific configuration, but `scripts/test-sql.ps1`
uses one fixed container name and the setup guide uses fixed SQL/API/Vite ports. GemInv's
`dev-up`, `dev-status`, and `dev-down` scripts provide a useful lifecycle precedent.

Provide that lifecycle with automatic provisioning, safe ownership, persistent local test
data, current-source builds, and a URL suitable for manual acceptance testing. Initial support
is PowerShell 7 on this Windows machine with Docker Desktop's Linux engine. Other platforms
are not claimed until tested. Existing verification fixtures and retained self-host installations
remain separate workflows.

## Recommended topology

Use a dedicated Compose project per checkout, containing SQL Server and the existing application
image, which serves both the built React UI and API. Publish only the application on a dynamically
assigned loopback port. SQL is reachable only on that project's internal network. Run database
tooling as short-lived containers on that network.

Each environment owns its SQL data volume, native Linux blob volume, network, credentials,
tenant proof key, installation UUID, and database-backed data-protection keys. Mount blob storage
with the runtime UID and the existing durable-volume configuration. Do not use Windows bind
mounts for container photo storage: Linux publication operations need compatible filesystem semantics.

Reuse the Dockerfile and database command contracts; introduce a separate development Compose
definition rather than changing the production Compose defaults. Run the application in Development
with its normal authentication and tenant enforcement. Real email delivery and production recovery
drills are outside this preview contract. A continuously running worker is deferred; workflows
requiring worker execution must identify that limitation and use the appropriate verification setup.

The first version is a built preview. `dev-up` builds current API and UI source together; edits
become visible after running it again. No Vite server or source watcher is required. This trades
live reload for simpler ownership, same-origin behavior, deployment-like packaging, and independence
from host build-output locks. Docker layer caching reduces repeated build cost.

## Commands and user experience

Run from any directory within the intended checkout; resolve the root before taking action.

| Command | Contract |
| --- | --- |
| `./scripts/dev-up.ps1` | Provision on first use; otherwise refresh the preview from current source, migrate forward if needed, start services, and verify readiness. |
| `./scripts/dev-status.ps1` | Read-only report of ownership, lifecycle state, URL, running build provenance, and source changes since that build. Nonzero exit unless the complete preview is ready. |
| `./scripts/dev-down.ps1` | Stop this environment's application and SQL; preserve all data, credentials, resource identity, and saved port preference. Idempotent. |
| `./scripts/dev-destroy.ps1 -EnvironmentId <id>` | Explicitly delete only this checkout's validated environment resources and local state. The ID must match; deletion is never implicit in another command. |

All commands support `-Json` for a documented, versioned, secret-free result. First-use optional
inputs are `-TenantName` and `-AdminEmail`, with sensible development defaults. There are no prompts
during startup. Generate the administrator password and SQL secrets; report only the administrator
email and the path to a protected local login file. The user can open that file to obtain the password.
Do not put passwords in chat, URLs, process arguments, general state, or logs.

On success print the environment ID, checkout, branch/commit, dirty-source fingerprint, browser URL,
administrator email, login-file path, and status/stop commands. The agent opens the URL, exercises the
changed workflow, and gives the user that URL with what was tested. Readiness alone does not establish
functional acceptance. URLs are local to this computer, not remote sharing links.

`dev-up` is idempotent when the recorded build input fingerprint is unchanged and all owned services
are healthy. Fingerprints cover actual Docker build inputs, including relevant untracked source,
not merely HEAD; exclude generated files and secrets according to an explicit build-input contract.
Record immutable image ID, fingerprint, commit, dirty flag, and build time. Detect source changes
during the build and fail/retry within a bound rather than claiming ambiguous build provenance.

## Identity, state, and concurrent operations

Store ignored state under `.dev-environment/`: a versioned manifest, generated Compose configuration,
private credential files, and an atomic operation journal in the manifest's phase/resource records. Protect secret files for the local
OS account before writing values. Explicitly exclude this directory from Git and Docker build context.
Do not read, adopt, or overwrite an existing `.env.dev` or the shared `workbench-test-sql` fixture.

Create an unpredictable environment ID and bind it to the canonical checkout path and Git worktree
metadata. Apply matching purpose, environment, and owner labels to every managed Docker resource;
use immutable resource IDs where available. A copied/moved state directory is not automatic authority
to operate on the original environment. Fail with a recovery explanation on an owner mismatch.

Use one OS-backed exclusive lock per canonical checkout for all mutations. Concurrent starts of the
same checkout serialize with a bounded timeout; different checkouts can start independently. Journal
intent before creating resources and record resource IDs immediately afterward. Write state atomically.
After a crash, reconcile journal, labels, and actual resources before resuming. Missing/corrupt ownership
evidence must not trigger name-based deletion or broad Docker cleanup.

Let Docker allocate the initial application host port and read back the binding. Save it as the
preferred port for subsequent starts. If unavailable, select a new free port through Docker and report
the changed URL. Handle binding collisions with bounded retries; do not kill another listener.
Bind explicitly to `127.0.0.1` and use `http://localhost:<port>` as the browser URL.

## Browser session isolation

Cookies are not isolated by port. Add an optional, strictly validated development environment ID
configuration setting. In Development only, use it to suffix both session and antiforgery cookie
names and the data-protection application discriminator. Without it, retain existing behavior.
Production cookie names, security flags, and data-protection contracts remain unchanged.

Set the exact development public origin if required by existing request/origin handling. Never weaken
antiforgery, authentication, or tenant checks to make the preview work. The two-preview acceptance test
must use one browser profile and prove that login, mutation, and logout in one do not disrupt the other.
This is accidental-state isolation on a trusted developer machine, not isolation from hostile local code.

## Provisioning, updates, and failures

1. Validate Docker CLI and engine separately, Compose, checkout ownership, and state format. Use the
   known per-user Docker Desktop path when absent from PATH; follow approved escalation on access denial.
2. Under the checkout lock, initialize or reconcile the environment and build the current image before
   interrupting an existing healthy preview. Build failures leave that preview running and marked stale.
3. Start owned SQL with its retained volume, wait for bounded SQL readiness, and initialize containment
   and the database only on a verified first installation.
4. Apply initial migrations and provision distinct web, operator, and migrator users using existing
   database tools. Bootstrap the tenant/admin once. Supply credentials through files and mount only
   the web credential and required proof material into the application.
5. For existing installations, stop the application before any migration; migrate with the migrator
   credential. Preserve credentials, installation ID, data, and keys. Compare actual migration history
   with the current image; reject divergent/newer histories rather than rewriting history or downgrading.
6. Start the selected immutable application image and verify its ownership, binding, UI response, and
   `/health/ready`. Record ready only after all checks succeed.

Migration failure leaves the application stopped and the database preserved, with a recoverable failed
phase in state. Bootstrap interruptions reconcile actual schema/principal/tenant state; never silently
regenerate secrets or bypass an initialized-installation refusal. Do not restart an older image after
a migration without establishing compatibility. Incompatible branch switches require a deliberate
disposable reset or a different worktree; automatic database restoration is outside this version.

Timeouts and startup failures return nonzero with the phase and secret-free diagnostics. Preserve data
and enough state to inspect/stop/retry. Stop only proven-owned resources started by the failed attempt;
record anything still running. Never claim cleanup succeeded when it did not. No pruning by name prefix,
process-wide termination, volume deletion during stop, or automatic orphan deletion.

## Alternatives and tradeoffs

- Host API plus Vite resembles GemInv and supports fast live reload, but adds detached process ownership,
  port/proxy coordination, and host/container storage differences. Defer until preview iteration times
  demonstrate the need; it can later share the same environment identity and database lifecycle.
- One shared SQL server with a database per checkout saves memory but shares availability, server settings,
  and cleanup authority. Dedicated SQL containers cost more memory but make ownership and schema experiments
  explicit. Measure two concurrent environments and report resource pressure rather than changing topology silently.
- A retained self-host installation provides HTTPS and operational services, but is too broad a lifecycle
  for disposable concurrent worktrees. Do not repurpose or mutate an existing retained installation.

## Acceptance and delivery

Use TDD with Gherkin comments for lifecycle decisions and development-only cookie behavior. Exercise
meaningful mutations in ownership, cleanup, readiness, and configuration gating; record tooling limits.
Required real Docker/browser acceptance:

- Fresh checkout startup needs no handwritten SQL or credential configuration and produces a usable login.
- Two worktrees start concurrently with separate SQL/blob state and URLs. Each can save a different item
  and upload/download a photo without cross-contamination or login-cookie interference.
- Starting an unchanged healthy environment reuses it. Changed API and UI source appear after `dev-up`,
  and reported provenance identifies the current build. A failing build preserves the prior preview.
- Closing the launching terminal preserves service availability. Down/up preserves records, photos,
  credentials, and key material. Explicit destroy affects only its matching environment.
- Same-checkout concurrent commands, occupied ports, interrupted provisioning, corrupt/copied state,
  foreign resource labels, migration divergence, and failed readiness all follow the failure contracts.
- A forward migration retains sample data; a newer/divergent schema refuses startup without destructive repair.
- Production authentication configuration and existing test/self-host lifecycles remain covered and passing.

Implementation delivery comprises lifecycle scripts/helpers, development Compose configuration, the
development isolation setting, tests, ignore rules, and a rewritten canonical `docs/setup.md` entrypoint.
Update CONTRIBUTING and agent guidance to use the scripts and report manual preview URLs. Run applicable
repository verification and container gates, then deliver a ready-for-review PR with external visual evidence.
No application schema migration is expected solely for this feature. Removing the tooling does not remove
existing Docker volumes; document explicit cleanup before abandoning a checkout.

## Implementation notes

The launcher shares Docker discovery, protected directory setup, bind-mount construction, connection
string construction, and database-container execution with the local self-host installer. The existing
database commands retain authority over migrations, principal validation, and atomic bootstrap. A
read-only `development inspect` command reports exact migration-prefix compatibility and global bootstrap
state under the setup identity; it does not change migration or provisioning behavior.

Destroy retains only a non-secret state tombstone and OS lock file, preventing concurrent commands from
opening different locks after directory deletion. Docker image/build cache remains reusable. State
phase/resource records provide the operation journal; raw subprocess output is suppressed because it
may contain credentials. Source inspection failures are reported separately and never block stop/destroy.

The build context is explicitly limited to Git-visible source beneath the three application projects
and root Docker/build configuration. Ignored files are not implicit build dependencies. If a future
Dockerfile needs additional roots, update the context inventory and its tests together.

## Verification record

- Two real worktree previews passed UI login, distinct item/photo storage, cross-environment 404,
  shared-browser cookie isolation, independent logout, and retained session/photo checks after stop/up.
  The final restart check opens the retained database before continuing: master readiness and ONLINE
  metadata alone can precede database accessibility during recovery.
- Changed API and UI source appeared after refresh. A deliberately invalid build preserved the previous
  usable image. Explicit destruction worked with missing source, preserved its tombstone on stop,
  and allowed fresh recreation without disrupting the other preview. The temporary worktree was removed.
- All 626 server cases passed in two partitions; 156 client tests, formatting, API generation, and
  published runtime checks passed. The aggregate verification run encountered another checkout on
  browser port 4179; the entire browser suite was rebuilt/rerun on an available port and all 56 passed.
  The separate container/Compose smoke gate passed.
- Sixteen lifecycle scenarios and the source/Compose/shared-runtime contracts passed, alongside the
  five existing self-host contract scripts. Targeted process-local mutations of ownership, cached-image
  selection, unchanged-image reuse, and database readiness were detected; three server isolation
  mutations were also detected. This is scoped mutation evidence, not a repository-wide mutation score.
- A snapshot of two previews used approximately 2.2 GiB combined, predominantly SQL Server. This is a
  single-machine observation, not a resource reservation or capacity guarantee.

Repeat browser acceptance with two ready disposable previews and an evidence directory outside Git:

```powershell
node tests/Workbench.BuildTests/DevPreview.Browser.mjs <first-checkout> <second-checkout> <evidence-directory>
```

This check reads the protected administrator files privately, saves synthetic records/photos, and stops
and resumes the first preview. It never prints credentials or saves browser traces containing them.
