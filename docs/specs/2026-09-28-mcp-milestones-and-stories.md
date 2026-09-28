# MCP milestones and agent handoff stories

Status: proposed delivery roadmap for the [MCP product spec](2026-09-28-mcp-agent-interface.md).
Story definitions authorize neither implementation nor deployment. They are inputs to future
agent-led brainstorming, design, implementation, and validation, with the repository's review gates.

## How to use this roadmap

Each story has a stable identifier, user outcome, scope, acceptance criteria, evidence, and hard
predecessors. Milestones group outcomes; they are not blanket barriers. A story may start when its
own prerequisites are met, even if another story in an earlier milestone remains unfinished.
Implementation predecessors mean integrated, verified behavior. Decision predecessors mean an
explicitly approved artifact. Mocked dependencies do not satisfy either condition.

Every handoff includes this roadmap, the parent product spec, approved prerequisite artifacts and
PRs, the current capability inventory, current repository guidance, and the selected story ID.
The assigned agent must recheck the current product rather than assume the roadmap describes
already implemented capabilities. No agent inherits approval for an unseen design or plan.

Each future story follows this lifecycle:

1. **Brainstorm:** confirm the intended outcome, current behavior, boundaries, and open decisions.
2. **Design:** produce and obtain review of the appropriate design artifact; obtain implementation
   plan review and execution selection when required by the repository workflow.
3. **Implement:** deliver the scoped behavior and documentation. Decision stories implement a
   reviewed decision artifact and any explicitly approved disposable compatibility probe, not a
   production subsystem. Split a story before implementation if discovery reveals separate outcomes.
4. **Validate:** exercise its acceptance criteria and relevant negative cases, update the capability
   inventory, and provide evidence from current source through a reviewable PR. Follow repository
   testing, mutation-testing, migration, and integration requirements as applicable.

Story completion is not milestone release approval. Production enablement remains a separate human
decision. Do not create GitHub issues, new chats, or start implementation merely from this roadmap.

## Milestones

| Milestone | Outcome / exit evidence | Stories |
| --- | --- | --- |
| M0 — Agree the contracts | Approved capability, role/approval, and client-authentication decisions make implementation scope concrete. | MCP-01–03 |
| M1 — Connect with explicit authority | A human can provision an isolated tenant principal, assign eligible predefined roles, connect a supported client, and revoke access. | MCP-04–07 |
| M2 — Make delegated operations trustworthy | Shared mutation outcomes, any required approval mechanism, and usable activity evidence support domain tools. | MCP-08–10 |
| M3 — Deliver business workflows | Supplier, collection, purchasing, accounting, and file/export stories cover the approved current-product inventory; unavailable capabilities remain explicit gaps. | MCP-11–22 |
| M4 — Demonstrate and release parity | Reconciled capability inventory and hosted/self-hosted client evidence establish parity for a named product revision. | MCP-23–24 |
| M5 — Sustain parity | New web capabilities and contract changes cannot silently leave agents behind. | MCP-25 |

M3 may ship useful increments. A supplier pilot is a useful first release candidate, not a
prerequisite for inventory or purchasing work. Full parity cannot be claimed while an eligible,
released web workflow remains deferred. Future product areas are handled by MCP-25 as they emerge.

## M0 — Agree the contracts

### MCP-01 — Know which workflows an agent can perform

**Story:** As a product owner, I want an operation-level capability inventory so that parity and
human-only exceptions are explicit and measurable.

- **Scope:** Inventory the current web product, including less-visible file, correction, archive,
  restore, export, and business-configuration operations. Record current versus future capabilities.
- **Acceptance:** Every workflow has a stable inventory ID, domain owner, eligibility reason,
  effect category, existing authority, proposed MCP coverage, and mapped story. Navigation placement
  alone never determines eligibility. Unimplemented internal accounting commands are not public scope.
- **Evidence:** Reconcile the inventory with current UI routes, API operations, and living guides;
  review every exclusion and uncovered operation with the owner.
- **Predecessors:** None. **Parallel:** MCP-02 and MCP-03; synchronize shared terminology before approval.

### MCP-02 — Delegate predefined roles with understandable limits

**Story:** As a tenant administrator, I want a reviewed role catalog and control policy so that I
can delegate useful work without granting identity or tenant-administration powers.

- **Scope:** Define predefined principal-eligible roles, defaults, role unions, grantor restrictions,
  role-expansion policy, provisioning-owner lifecycle, activity visibility/retention, and which
  operations require independent approval. No custom roles or per-principal permission editor.
- **Acceptance:** A permission matrix covers MCP-01's eligible operations; no role grants excluded
  powers. No-role and read-only cases are explicit. Business accounting configuration is classified
  separately from identity administration. Approval-required operations and approver authority are
  explicit, including whether the first release needs any approval workflow at all.
- **Evidence:** Walk through read-only reporting, supplier maintenance, financial authority, grantor
  escalation, administrator departure, and a role gaining a permission in a later release.
- **Predecessors:** None to explore; approved MCP-01 inventory required to finalize the matrix.
  **Parallel:** MCP-01/MCP-03 exploration; no unresolved matrix entries at handoff to implementation.

### MCP-03 — Connect real clients using workload identity

**Story:** As an integration owner, I want a supported connection and credential model so that an
agent can authenticate as its principal without using my browser session or password.

- **Scope:** Select at least two representative clients and a supported protocol/transport and
  credential lifecycle for local, self-hosted, and hosted deployments. Decide issuance, secure
  storage, resource binding, expiry, rotation, revocation, and bounded credential overlap.
- **Acceptance:** Evidence establishes that chosen clients support the selected workload flow;
  user OAuth support is not treated as proof of service-principal support. Explain unsupported
  combinations and resolve material conflicts with the parent architecture recommendation.
- **Evidence:** Official client/protocol documentation and an approved isolated probe where claims
  need runtime proof. No production credentials or infrastructure changes.
- **Predecessors:** None. **Parallel:** MCP-01/MCP-02.

## M1 — Connect with explicit authority

### MCP-04 — Represent a tenant service principal throughout authorization

**Story:** As a tenant owner, I want agents to have distinct tenant-owned identities so that their
authority and changes cannot be confused with a human user's.

- **Scope:** Principal identity, predefined role assignments, lifecycle state, actor attribution,
  grantor checks, and shared application/persistence authorization contracts. Preserve human history.
- **Acceptance:** No synthetic human session or creator impersonation. Tenant binding is immutable;
  role unions, no-role denial, excluded-operation denial, and current-authority checks hold at the
  protected command boundary. Define serialization with access changes and preserve existing users.
- **Evidence:** Real persistence tests for tenant substitution, actor confusion, role removal races,
  excluded powers, and fresh/upgrade schema behavior; current human workflows still pass.
- **Predecessors:** MCP-01, MCP-02. **Parallel:** Other M0 decision work; domain design may explore
  independently, but must not freeze incompatible actor or SQL contracts.

### MCP-05 — Issue, rotate, and revoke principal credentials

**Story:** As an integration owner, I want a credential lifecycle so that agents can connect and I
can replace or revoke their access without changing human credentials.

- **Scope:** Implement MCP-03's approved credential model over MCP-04; production secrets stay out
  of tool results, logs, source control, and browser storage.
- **Acceptance:** Invalid, expired, revoked, wrong-resource, and disabled-principal credentials fail.
  Rotation follows the approved overlap policy. Replica changes do not restore revoked access.
  Removing roles changes authority even when authentication credentials remain valid.
- **Evidence:** Cross-replica authentication/revocation and clock/expiry boundary checks, safe setup
  diagnostics, and restart persistence; no live production secret required.
- **Predecessors:** MCP-03, MCP-04. **Parallel:** MCP-08/MCP-09/MCP-10 after their prerequisites.

### MCP-06 — Manage agent access through the human web interface

**Story:** As an authorized administrator, I want to create, inspect, disable, and manage roles and
credentials for principals so that delegated access remains understandable and controllable.

- **Scope:** Human-only principal management, effective-access display, approved defaults, role
  assignment/removal, credential setup/rotation/revocation, and provisioning-owner review.
- **Acceptance:** Human grantor boundaries are enforced server-side; principals cannot invoke these
  operations. Disabled/no-role states are clear. Setup reveals exactly the access being granted;
  sensitive credential display follows MCP-03 and is not retrievable through ordinary audit screens.
- **Evidence:** Browser journeys plus direct unauthorized requests, cross-tenant access attempts,
  stale role edits, and resulting access changes from a real principal credential.
- **Predecessors:** MCP-04, MCP-05. **Parallel:** MCP-07–10; all consume approved shared contracts.

### MCP-07 — Connect and discover permitted MCP capabilities

**Story:** As an agent operator, I want a working endpoint with useful capability discovery so that
my client knows what the principal can do and how to recover from errors.

- **Scope:** Native HTTP MCP hosting, supported protocol negotiation, connection diagnostics,
  authorization-aware discovery, bounded requests/results, rate limits, and common error contracts.
- **Acceptance:** A client can identify its own principal/effective access without enumerating hidden
  data. Hidden tools remain protected when invoked directly. Unsupported versions fail clearly;
  client-supplied tenant/actor metadata is never authority. Health and feature enablement are explicit.
- **Evidence:** Protocol/client contract checks, direct invocation bypass attempts, malformed/oversized
  requests, revocation after discovery, and a real supported-client connection.
- **Predecessors:** MCP-01, MCP-03, MCP-05. **Parallel:** MCP-06 and MCP-08–10.

## M2 — Make delegated operations trustworthy

### MCP-08 — Know whether a delegated change completed

**Story:** As an agent operator, I want durable operation outcomes so that a timeout or concurrent
edit does not cause duplicated work or silent overwrites.

- **Scope:** Shared version, retry-identity, receipt/status, error, cancellation, and bounded-batch
  conventions. Reuse existing domain guarantees; avoid a second generic writer above domain commands.
- **Acceptance:** Same input/identity yields one effect; changed input conflicts. Stale versions
  cannot overwrite newer changes. Define atomic versus partial batch results and retry ownership.
  Status and receipt reads remain authorized, including after role/credential changes.
- **Evidence:** An executable reference against an existing protected command, lost-response and
  concurrent-request tests, partial-batch recovery, and preservation of existing human retry behavior.
- **Predecessors:** MCP-04. **Parallel:** MCP-05–07/MCP-09/MCP-10; agree result schemas before consumers code.

### MCP-09 — Obtain independent approval where business policy requires it

**Story:** As a business owner, I want required approvals bound to exact proposed changes so that
an agent cannot approve its own work or reuse approval for a different action.

- **Scope:** Only approval categories selected by MCP-02: preparation, human decision, status,
  expiration, changed-input invalidation, and execution-time authorization. Ordinary role-authorized
  edits gain no blanket confirmation requirement.
- **Acceptance:** Bind approval to tenant, principal, operation, input and relevant versions. Denial,
  expiry, access removal, or material change blocks execution. Human approval cannot be forged by
  tool input. Define reuse/retry semantics, including a successful operation retried after expiry.
- **Evidence:** Cross-principal replay, changed-record/input, revoke-versus-execute and duplicate
  execution checks; human approval and agent status journeys.
- **Predecessors:** MCP-02, MCP-04, MCP-08. **Parallel:** MCP-06/MCP-07/MCP-10 and domain reads.
  If MCP-02 selects no approval-requiring released operations, record this story as not applicable
  for that release; reopen it before any later operation requiring approval ships.

### MCP-10 — Inspect agent activity without exposing secrets

**Story:** As a business reviewer, I want to see who delegated access and what each principal did
so that I can investigate changes and uncertain outcomes.

- **Scope:** Human activity views, permission-filtered principal activity/status access, business
  receipt links, administration events, safe diagnostics and the retention policy from MCP-02.
- **Acceptance:** Server-authenticated actor attribution is distinct from client-claimed labels.
  Show affected records and outcome without recording credentials or full prompts/documents by
  default. Retention never destroys required business receipts or immutable financial evidence.
- **Evidence:** End-to-end event/readback checks, denied activity access, secret-canary exclusion,
  retention behavior, and traceability across retries and role changes.
- **Predecessors:** MCP-02, MCP-04, MCP-08. **Parallel:** MCP-06/MCP-07/MCP-09 and domain stories.

## M3 — Deliver business workflows

All domain stories inherit the actor, tenant, role and human-only boundaries. Each implements its
MCP-01 inventory rows through shared business behavior, with structured validation and equivalent
web outcomes. Each write story depends additionally on MCP-09 if its approved policy requires
independent approval. Each produces MCP-10-compatible events and is not release-complete until
MCP-10 readback is verified. These conditional dependencies are part of the dependency graph.

### MCP-11 — Find and understand suppliers

**Story:** As a purchasing assistant, I want complete supplier lookup so that I can identify existing
contacts and avoid accidental duplication.
- **Acceptance/scope:** Search, paginated listing including archived selection, detail/contact/profile
  reads, stable IDs and versions; no cross-tenant disclosure or unbounded list responses.
- **Evidence:** Page completeness with duplicate names, archived records, restricted roles and
  concurrent list changes under the documented consistency contract.
- **Predecessors:** MCP-07. **Parallel:** MCP-14/MCP-16/MCP-19/MCP-20.

### MCP-12 — Maintain supplier records

**Story:** As a purchasing assistant, I want to create, edit, archive and restore suppliers so that
the directory can be maintained without browser automation.
- **Acceptance/scope:** Eligible supplier mutations preserve existing IDs, contact snapshots on
  historical purchases, current validation and explicit field-clearing semantics. Conflicts preserve
  recoverable input. Retries do not create another supplier.
- **Evidence:** Shared web/MCP behavior, stale writes, duplicate retry and archive/restore checks.
- **Predecessors:** MCP-08, MCP-11. **Parallel:** MCP-15/MCP-17/MCP-21/MCP-22.

### MCP-13 — Preview and reconcile a supplier merge

**Story:** As a business owner, I want to review and apply a source-neutral supplier merge so that
I can migrate contacts while preserving richer existing records.
- **Acceptance/scope:** Bounded structured source input, explicit candidate matching, ambiguous-match
  resolution, per-field preserve/replace policy, preview and apply outcomes, source-to-target mapping,
  and resumable reconciliation. Name similarity alone never authorizes a match. Revalidate versions
  and authority; declare batch atomicity. Shared behavior has a corresponding human workflow.
- **Evidence:** Synthetic 34-source/11-match/23-create scenario, interrupted retries, duplicate source
  rows, conflicting fields, concurrent target creation/edit and failed-row recovery. No arbitrary
  SQL, local-backup parsing, or server filesystem access.
- **Predecessors:** MCP-12; applicable MCP-09 policy. **Parallel:** Other domain tracks.

### MCP-14 — Inspect a collection

**Story:** As a collection assistant, I want inventory search and complete item details so that I
can answer questions using authoritative records.
- **Acceptance/scope:** Active/archived search, pagination, classifications, provenance, locations and
  supported detail fields, version identity and attachment metadata; missing facts remain unknown.
- **Evidence:** Query/filter parity, pagination, inaccessible IDs and representative item types.
- **Predecessors:** MCP-07. **Parallel:** MCP-11/MCP-16/MCP-19/MCP-20.

### MCP-15 — Maintain collection records

**Story:** As a collection assistant, I want to create, edit, archive and restore items so that
delegated catalog work is immediately usable in the web application.
- **Acceptance/scope:** Cover MCP-01's released descriptive inventory mutations and classifications;
  preserve versions and distinguish descriptive changes from movement/financial events. Binary
  attachment transfer belongs to MCP-20, acquisition relationships to MCP-22.
- **Evidence:** Representative item-type edits, explicit clearing, stale-write/retry behavior,
  archive/restore and independent web readback.
- **Predecessors:** MCP-08, MCP-14. **Parallel:** MCP-12/MCP-17/MCP-21/MCP-22.

### MCP-16 — Inspect purchases and calculate proposals

**Story:** As a purchasing assistant, I want to read orders and calculate draft estimates so that
I can explain a purchase before changing it.
- **Acceptance/scope:** Draft/ordered lists, detail, supplier snapshots, amendments/history and
  supported calculations. Preserve exact decimals, currencies, unknown amounts and rounding.
  Calculation does not commit an order or create financial effects.
- **Evidence:** Shared calculation vectors, incomplete lines, zero versus unknown, filtering,
  pagination, state conflicts and restricted access.
- **Predecessors:** MCP-07. **Parallel:** MCP-11/MCP-14/MCP-19/MCP-20.

### MCP-17 — Prepare and maintain purchase drafts

**Story:** As a purchasing assistant, I want to create, edit and remove drafts so that a human can
continue the same purchase in Workbench.
- **Acceptance/scope:** Supplier selection/snapshots, lines, estimates, discounts, charges, notes,
  source references and current draft deletion behavior. Preserve permanent references and retry
  evidence; reject mutation of committed orders. Attachments use MCP-20.
- **Evidence:** Web/MCP draft round trips, retained legacy content, stale writes, retry after deletion,
  invalid totals and draft-versus-ordered state checks.
- **Predecessors:** MCP-08, MCP-11, MCP-16. **Parallel:** MCP-12/MCP-15/MCP-21/MCP-22.

### MCP-18 — Commit and amend supported purchases

**Story:** As an authorized purchasing assistant, I want to perform released order commitment and
amendment transitions so that delegated purchasing follows the same business rules as the web.
- **Acceptance/scope:** Only current public commitment/amendment operations identified by MCP-01;
  enforce their state machines, actor authority, protected history and independent approval policy.
  Internal receipt, payment, posting or correction procedures do not become public through this story.
- **Evidence:** Authorized/denied transitions, stale commitments, concurrent amendment, exact retries
  after later state changes, history readback, and applicable approval checks.
- **Predecessors:** MCP-17 and applicable MCP-09. **Parallel:** Other domain writes after prerequisites.

### MCP-19 — Inspect and configure supported accounting capabilities

**Story:** As an authorized accounting assistant, I want the released accounting configuration and
read workflows so that I can explain and maintain business setup within assigned roles.
- **Acceptance/scope:** The exact MCP-01 inventory of public accounting setup reads and mutations,
  including permitted account/configuration lifecycle operations. Reporting authority does not imply
  configuration authority. Do not activate bookkeeping or expose internal journal/period commands.
- **Evidence:** Role matrix, invalid configuration, concurrent revision/retry and web/MCP equivalence;
  demonstrate that unavailable posting remains unavailable. If the inventory reveals independently
  substantial configuration and reporting outcomes, split before implementation.
- **Predecessors:** MCP-07, MCP-08 and applicable MCP-09. **Parallel:** Supplier/collection/purchasing.

### MCP-20 — Exchange private documents and photographs

**Story:** As an agent operator, I want authorized binary transfer and retrieval so that agents can
use the same evidence as people without embedding large files in conversations.
- **Acceptance/scope:** Shared bounded transfer contract and adapters for the currently released item,
  acquisition and purchase-document workflows, including eligible replacement/removal and metadata.
  Scope each resource separately; preserve integrity, lifecycle, current role checks and limits.
  Partial uploads and downloads do not masquerade as completed evidence.
- **Evidence:** Real binary round trips, role removal during transfer, cross-tenant references,
  corrupt/oversized payloads, interrupted publication, and provider portability. Split owner-specific
  adapters into child stories if scope discovery warrants it, with one shared transfer-contract owner.
- **Predecessors:** MCP-07, MCP-08. **Parallel:** Domain tracks; integrate adapters with their owners.

### MCP-21 — Produce and retrieve portable exports

**Story:** As a data owner, I want agents to request and retrieve supported exports so that I can
use my records outside Workbench without public download links.
- **Acceptance/scope:** Released collection/acquisition export formats and any other exports approved
  in MCP-01, with correct inclusion boundaries, bounded execution, completion/status, private retrieval
  and cancellation/expiry semantics. No backup restore or new financial report format.
- **Evidence:** Existing format/manifest checks, equivalent web selection, denied cross-tenant retrieval,
  interrupted requests and duplicate-retry outcomes, including metadata-only versus binary packages.
- **Predecessors:** MCP-08, MCP-14, MCP-20; MCP-22 for acquisition-dependent workflows.
  **Parallel:** Supplier and purchasing tracks once those prerequisites are satisfied.

### MCP-22 — Maintain acquisition context and relationships

**Story:** As a collection owner, I want an agent to maintain released acquisition records and item
relationships so that provenance remains coherent across collection and purchase information.
- **Acceptance/scope:** Current acquisition read/write and link/unlink operations from MCP-01; preserve
  tenant-qualified relationships, source facts and existing correction rules. Do not manufacture
  payment, receipt or ledger activity. Documents use MCP-20; exports use MCP-21.
- **Evidence:** Multi-item associations, invalid/cross-tenant links, concurrent relationship edits,
  retries and independent web readback.
- **Predecessors:** MCP-08, MCP-14. **Parallel:** MCP-12/MCP-15/MCP-17/MCP-19/MCP-20.

## M4 — Demonstrate and release parity

### MCP-23 — Reconcile capability coverage and cross-interface behavior

**Story:** As a product owner, I want evidence tied to a named product revision so that a claim of
MCP parity means every eligible workflow is actually usable.
- **Acceptance/scope:** Reconcile every MCP-01 row with implemented tools, roles, web behavior and
  evidence. Account for new web features since the inventory baseline. Exercise human-to-agent and
  agent-to-human continuation, revocation, retry and approved action scenarios across domains.
  Resolve missing rows through new bounded stories; never close this story by silently excluding them.
- **Evidence:** Versioned coverage matrix, representative cross-interface journeys, negative access
  matrix and current-source repository gates. Individual domain stories own their deeper regression tests.
- **Predecessors:** MCP-06–10 (MCP-09 conditional), every applicable MCP-11–22, MCP-25, and any inventory gap stories.
  **Parallel:** MCP-24 preparation; final acceptance requires integrated current-source evidence.

### MCP-24 — Operate the supported MCP product

**Story:** As a self-hosting or hosted-service operator, I want documented setup and recovery from
integration failures so that users can connect reliably and revoke access when necessary.
- **Acceptance/scope:** Supported-client setup, canonical endpoint configuration, safe diagnostics,
  credential lifecycle guidance, rate/resource limits, health, upgrade/rollback compatibility,
  retention and emergency disablement. Validate at least two selected clients across hosted and
  self-hosted profiles. No protocol, deployment or credential policy guesses remain at release.
- **Evidence:** Disposable deployment rehearsals, connection/rotation/revocation across restart and
  replicas, upgrade compatibility, safe diagnostic output and a documented operator walkthrough.
- **Predecessors:** MCP-06, MCP-07, MCP-10 for preparation; MCP-23 for full-parity release acceptance.
  **Parallel:** M3 domain delivery and MCP-23 evidence preparation. Production activation is separately authorized.

## M5 — Sustain parity

### MCP-25 — Keep new business features and role changes compatible

**Story:** As a maintainer, I want parity and access-impact checks in feature delivery so that web
enhancements and role changes cannot silently break integrations or expand delegated authority.
- **Acceptance/scope:** Establish the capability-inventory owner, feature/PR checklist, schema and
  compatibility evidence, and role-expansion review procedure. Exercise the process with an example
  new feature and an example role change. Future receiving, invoicing, payments, reconciliation,
  period operations, work orders and commerce each require their own scoped MCP stories when their
  public business contracts exist. This story does not implement those future product areas.
- **Evidence:** A reviewable process and checks that detect a missing MCP mapping, incompatible
  contract change, or unreviewed permission expansion without brittle endpoint-count assertions.
- **Predecessors:** MCP-01–03. **Parallel:** M1–M4. Required before declaring M4 complete, despite
  its ongoing-maintenance milestone label.

## Parallel execution and integration rules

| Work available | Safe parallel assignments | Wait conditions |
| --- | --- | --- |
| Initial discovery | MCP-01 and MCP-03; MCP-02 exploration | MCP-02 approval waits for MCP-01's finalized inventory. |
| Identity foundations accepted | MCP-05 and MCP-08; MCP-25 can run separately | MCP-04 owns shared actor/role/SQL authority changes. |
| Credential and result contracts available | MCP-06, MCP-07, MCP-09, MCP-10 | Respect individual story predecessors; approval design does not block ordinary reads. |
| MCP endpoint available | MCP-11, MCP-14, MCP-16; MCP-19 and MCP-20 when MCP-08 is ready | Reads require real authentication and principal authorization, not mocked access. |
| Domain read/result contracts accepted | Supplier MCP-12, inventory MCP-15, draft MCP-17, acquisition MCP-22 | Domain writes needing approval also wait for MCP-09. |
| Domain foundations integrated | Merge MCP-13, commitments MCP-18, exports MCP-21 | Exports wait for binary transfer and applicable acquisition behavior. |
| Release preparation | MCP-23 and MCP-24 preparation; MCP-25 if not already complete | Full-parity acceptance waits for all applicable domain gaps and MCP-25. |

Parallel **brainstorming and read-only investigation** can begin earlier than parallel implementation,
but designs must be reconciled against approved predecessor contracts before they are treated as
implementation-ready. A contract change reopens affected downstream design assumptions.

Each implementation agent uses a separate managed worktree/branch. One owner integrates changes
to shared actor types, permission catalogs, schema migrations, MCP registration/contracts, and
shared transfer/outcome code. Agents do not independently redefine those surfaces. Serialize Git,
migration consolidation, shared database changes, and gates that contend for the same resources.
Rebase onto integrated prerequisites and rerun affected verification before combining results.

## Definition of a handoff-ready story

Before dispatch, record the story ID, exact inventory rows, integrated/approved predecessor
revisions, allowed scope, exclusions, unresolved questions, and required evidence. Add parent-spec
and current-code links. If an inventory gap is outside a story's stated boundary, create a proposed
child/follow-up story with its own ID and dependencies; do not silently grow another agent's task.

A completed handoff returns its approved design, implementation PR, validation evidence, inventory
updates, compatibility implications, and remaining limitations. A decision-only story returns its
approved decision artifact and supporting evidence. Neither a successful isolated test nor another
agent's completion message establishes integrated milestone completion.
