# Local self-host updates

Status: implemented. See the [verification record](../operations/local-self-host-update-verification.md).

## Problem and scope

The Windows local self-host installer creates retained SQL, blob, certificate, and
credential state but refuses existing roots. Users need one repeatable update command
without reconstructing Docker and database operations from chat history.

Add `scripts/update-local-self-host.ps1` for installations produced by the setup
script. Public production deployments, the older manual QA installation, SQL/Caddy
upgrades, certificate rotation, restore automation, and zero-downtime updates are
outside this change. Existing generated installations must work without reinstalling.

## Interface

Require `-SourceRef` naming a locally available committed release. Accept
`-InstallationRoot`, defaulting to the setup default, and an explicit
`-Confirmation 'UPDATE Workbench'` acknowledging downtime, backup, and migration.
Fetching remains explicit; never silently choose a newer remote revision. No tenant,
administrator, or password input is required. Reject unsupported/customized deployment
shapes with a concrete diagnostic before stopping workloads.

## Shared implementation

Extract focused helpers under `scripts/local-self-host/` for Docker discovery and
execution, clean-environment validation, immutable Git archive/image building,
mount construction, role-specific database execution, and bounded app readiness.
Pass installation context explicitly rather than relying on caller-local variables.
Both setup and update use these helpers; preserve setup behavior with characterization
coverage. Keep fresh provisioning and update orchestration separate.

Use the existing generated Compose configuration as installation state, preserving
networks, volumes, ports, identity, secret mounts, and dependency image pins. Do not
regenerate it from setup defaults. Preserve the original source directory because SQL
mounts its configuration from there. Store candidate source and release evidence in
separate directories. Change only the supported application release fields for both
app and worker. Retain the previous configuration and immutable image reference.

## Update sequence

1. Acquire an exclusive installation operation lock. Validate completed setup state,
   paths, required files, resource ownership, supported configuration, Docker Linux
   mode, current immutable image identity, and trusted HTTPS readiness. Reject an
   unresolved earlier update. Resolve and build the candidate before downtime.
2. Validate the candidate configuration and record a durable update journal with old
   and candidate revisions/images and the current phase. Preserve historical setup
   evidence. Stop proxy, app, and worker, and verify no installation writers remain.
3. Create a new protected checkpoint outside source control while writers stay stopped:
   SQL COPY_ONLY backup with checksum and VERIFYONLY; the existing database tool's
   storage snapshot and exact manifest; installation configuration and the certificate
   and secret material needed for recovery. Pair these artifacts in one catalog entry
   with hashes and source identity. Only complete checkpoints qualify for migration.
   Restrict access to the current Windows user and SYSTEM. Document that a local
   checkpoint is not off-host disaster recovery, and verification is not a restore drill.
   Use a fresh Linux Docker volume for blob snapshot publication: Windows bind mounts
   reject the required no-replacement atomic rename. Export into the protected Windows
   checkpoint, independently verify each blob length/digest, and retain the snapshot
   volume identity in the journal/catalog. Do not silently downgrade storage durability.
4. Run the candidate database tool's migrate command with only the migrator credential
   and SQL trust material. Never rerun bootstrap or regenerate principals/secrets.
5. Atomically publish the candidate Compose file. Start app and wait for readiness;
   run the worker once successfully, then start continuous worker and proxy. Verify
   trusted HTTPS readiness and workload state before marking the update successful.
6. Record success and print the local URL, checkpoint location, and browser verification
   instructions. Preserve previous release and checkpoint artifacts; no automatic pruning.

## Failure and recovery

Failures before stopping workloads leave the running installation unchanged. Once
downtime begins, any failure keeps public workloads stopped, preserves all artifacts,
and records the last phase plus a non-secret diagnostic. A process interruption leaves
an incomplete journal that blocks blind reruns. Startup recovery instructions must
distinguish pre-migration failure from a possibly applied migration.

Do not automatically revert images, run down migrations, restore the database, delete
volumes, or resume a partially completed update. Reviewed forward recovery or the
existing paired restore and sanitation procedure handles post-migration failures.
If stopping workloads fails, report that their state is uncertain rather than claiming
the installation is offline. Release the process lock on exit, retaining the journal.

This deliberately chooses an automatic local checkpoint over requiring users to
assemble and attest an external backup. It chooses a stopped failure state over
automatic rollback because shipped migrations can prohibit rollback.

## Acceptance and verification

- TDD covers successful update ordering, existing-installation compatibility, unchanged
  identity/secrets/volumes/dependency pins, invalid inputs and ownership, concurrency,
  incomplete journal rejection, and failures at build, stop, backup, migration, app,
  worker, and HTTPS stages. Failed checkpoints never allow migration or service restart.
- Setup characterization and orchestration checks remain green after extraction.
- Exercise meaningful mutations of phase gates and preservation checks; distinguish
  targeted injected mutations from any unavailable general mutation tooling.
- Run deployment static checks and documentation validation. Rehearse setup then update
  on an isolated disposable Docker installation with retained tenant data and nonempty
  blob content; verify login, data, schema, checkpoint contents, and failure containment.
  Do not use the user's retained installation as a test target.
- Report live checks separately from simulated Docker tests and restore drills. Update
  the local self-host guide with invocation, downtime, supported scope, checkpoint
  handling, and actionable failure recovery instructions.
