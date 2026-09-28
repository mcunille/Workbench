# Workbench MCP agent interface

Status: product and architecture proposal for review. No implementation is authorized by this document.

## Product vision

Workbench supports both direct human work and delegated agent work. MCP is a first-class
interface to the same business capabilities, records, and rules as the web application.
A user can ask an agent to investigate, prepare, or carry out work, then inspect and continue
that work in the web application. Neither interface owns a separate version of business truth.

The target is complete coverage of agent-eligible web functionality. This includes reading,
creating, editing, searching, calculating, attaching evidence, exporting, and performing
supported business transitions. It includes future functionality as it becomes available.
Parity means equivalent business outcomes and safeguards, not one tool per page or button.

Interactive sign-in and tenant/identity administration are deliberate human-only exceptions.
An MCP connection authenticates as a service principal; it does not sign in as a person or
inherit the permissions of an open browser session.

This follows the [vision](../VISION.md) and [design principles](../DESIGN-PRINCIPLES.md),
particularly progressive capability, tenant isolation, explainable changes, and one open-source
product across local, self-hosted, and hosted deployments.

## Decisions established with the owner

1. MCP is an enduring product interface, with full eligible business-functionality parity as
   its destination. Supplier migration is a motivating scenario, not the scope ceiling.
2. Each connection uses a service-principal identity to which authorized humans assign roles.
3. Workbench supplies predefined roles. Users can add and remove role assignments; custom
   role definitions and arbitrary per-principal permission editing are outside this proposal.
4. Setup offers sensible defaults and makes the resulting access visible.
5. Interactive sign-in and tenant administration are unavailable through MCP.

The recommendations below make these decisions concrete. Proposed defaults, role composition,
credential mechanisms, and approval policy still require review before implementation planning.

## People and outcomes

- A collector delegates catalog maintenance and research while retaining control over access.
- A business assigns separate principals to purchasing, inventory, or reporting assistants.
- An authorized administrator can understand, reduce, disable, and revoke an integration's access.
- A reviewer can identify what a principal changed, the operation's outcome, and its provenance.

Success is measured by completing eligible workflows without browser automation, correctness
under retries and concurrent edits, and understandable evidence of authority and outcomes.
Tool-call counts and latency matter, but never substitute for those properties.

## Capability boundary and parity

Maintain a capability inventory with the web workflow, corresponding MCP operation or operations,
required permissions, principal eligibility, effect category, and parity status. Each eligible
capability is implemented, explicitly deferred, or blocked with a reason. An omitted tool is a
coverage gap, not an implicit product exclusion.

| Capability family | Intended MCP coverage |
| --- | --- |
| Collection and inventory | Search, read, create, edit, archive/restore, classification, acquisition information, and supported transformations as released. |
| Suppliers and purchasing | Supplier maintenance; drafts, calculations, commitments, amendments, documents, receipts, and corrections as released. |
| Accounting | Authorized business configuration, reporting, reconciliation, posting and period operations as they become supported product workflows. |
| Evidence and portability | Authorized document/photo upload and retrieval, metadata, export creation, and result retrieval. |
| Future work orders and commerce | Equivalent supported business workflows when those product areas ship. |
| Identity and tenant control | Human-only: sign-in, account recovery, invitations, membership, user-role administration, tenant lifecycle, service-principal management, and credential issuance. |
| Platform operations | Deployment, database migration, backup restoration, infrastructure and platform-operator authority are outside the business MCP interface. |

Accounting configuration is business functionality, distinct from tenant identity administration.
An operation's location under an Administration navigation item does not determine eligibility.
The inventory must identify concrete operations rather than classify whole screens.

MCP must not activate unfinished functionality or bypass readiness gates. For example, the
current internal accounting foundation does not establish public financial-write capabilities.
An available internal procedure is not automatically an eligible product operation.

New business features include web/MCP coverage in their acceptance criteria. Incremental delivery
is allowed, with visible gaps, until parity is achieved; it must not silently redefine parity.

## Service-principal identity and access

A principal belongs permanently to one tenant and has its own stable identity, display name,
enabled/disabled state, role assignments, credential lifecycle, and audit history. Its creator
and subsequent access administrators are recorded as humans, separate from the principal actor.
Human account ownership and lifecycle changes must not silently transfer principal authority.

Recommend a distinct principal per integration or independently governed agent deployment.
An individual conversation is not a new principal, and clients cannot invent a human actor.
Client labels and correlation identifiers are useful provenance, not authentication evidence.

Effective authority is the union of the principal's currently assigned predefined role permissions,
intersected with service-principal eligibility and current business-state constraints. Authority
does not depend on the creator's current browser session. Changes to the creator's account do not
silently grant or remove access; administrators can identify and disable affected integrations.

Use the same permission vocabulary and authorization rules across web and MCP. Roles containing
human-only identity or tenant-control powers must not be assignable to principals. A hard
principal-eligibility check additionally rejects excluded operations even if a role definition
or assignment is misconfigured. Principals cannot manage themselves or other principals.

Only explicitly authorized humans manage principals. Administrative delegation must enforce the
grantor's permitted assignment set and must not allow a user to manufacture greater authority
through a principal. Human-only setup shows the selected roles and effective capabilities before
issuing access. Removing all roles leaves the principal with no business-data access.

Recommended default: creation starts with no access selected and offers a clearly described
read-only role preset appropriate to the user's intended domain. Write access requires explicit
selection. Domain reader/editor roles are preferable to a default all-powerful agent role.
Exact role names and permission composition are a separate, reviewable catalog decision.

Predefined does not mean silently expanding authority. Releases that expand a role's powers must
identify affected grants and define an explicit adoption policy; a new high-impact permission
must not arrive on existing principals as an incidental upgrade.

Disabling a principal, revoking a credential, or removing a role affects subsequent authorized
operations across replicas. Writes recheck current authority at the protected business command
boundary. A prepared operation confers no standing authority to execute it after access changes.
Already committed work remains committed; revocation cannot retrospectively cancel it.

## Recommended architecture

Host the MCP endpoint alongside the HTTP API in the existing Workbench application/release unit.
Both transports call shared application commands and queries, which invoke the existing protected
persistence mechanisms. Business calculations, transitions, validation, and authorization must not
be reimplemented inside MCP tools or delegated to model judgment.

The existing actor model carries `UserId`, `TenantId`, and `SessionId`. The implementation must
introduce explicit human/service-principal actor semantics through application authorization,
SQL command checks, receipts, and audit references. Creating a synthetic human session or attributing
principal writes to its creator would conceal authority and is not an acceptable shortcut.
Existing human history must retain its meaning through this evolution.

The server derives tenant context from authenticated principal state. A caller-supplied tenant,
record identifier, request header, or model instruction cannot establish authority. Preserve SQL
row-level security, tenant-qualified relationships, restricted commands, and separation of runtime,
operator, and migrator credentials. MCP receives no general SQL or filesystem execution capability.

Use a remote HTTP MCP interface so the same product serves hosted and self-hosted installations.
Transport interoperability and workload authentication must be designed together. Service-principal
identity is the authorization model; it does not by itself select an OAuth grant or credential format.
Evaluate supported client authentication flows before selecting credential issuance, exchange,
rotation, expiry, and revocation mechanisms. Do not assume every MCP client supports workload OAuth.
Browser cookies, human passwords, and database credentials are not integration credentials.

All integrations receive destination-bound credentials with a documented lifecycle. Secrets are
shown/transferred only through the human-authorized setup flow and retained securely; logs and tool
results exclude credential material. The MCP server validates the intended resource and current
principal authority. Client protocol metadata and tool annotations are not security controls.

### Alternatives considered

| Approach | Assessment |
| --- | --- |
| Native MCP transport over shared application behavior | Recommended: one release unit and one business-policy implementation; requires explicit actor-model evolution. |
| Separate MCP gateway over an authenticated integration API | Viable if independent deployment becomes necessary; adds network, identity, compatibility and operational boundaries. |
| Generate tools directly from HTTP endpoints | Useful scaffolding, but insufficient as the product design: browser-shaped requests do not establish good tool semantics, principal authorization, or workflow coverage. |

## Agent experience and operation contracts

Agents can discover supported capabilities and their own effective access without enumerating
other tenants or hidden records. Tools use domain language, bounded structured inputs and outputs,
stable identifiers, explicit version tokens, pagination, and field-specific validation errors.
Results make the difference between missing, unknown, zero, denied, stale, and unavailable explicit.
Monetary amounts and quantities retain the domain's exact representations and rounding rules.

Read and preparation operations are distinguishable from writes and irreversible transitions.
Preparation may calculate or persist a bounded proposal, but cannot silently execute its business
effects. Preparation storage has explicit ownership, lifetime and resource limits.
Agents can prepare and explain changes, resolve ambiguity, execute authorized operations, and
retrieve authoritative outcomes. Neither the model's prose nor a success-shaped tool message
substitutes for a server-confirmed result.

Support efficient bounded batches where they improve real workflows. Each batch contract declares
atomicity, limits, partial outcomes, cancellation behavior, and retry identities. Tool transport
batching does not imply a database transaction. A lost connection does not prove a save failed.
Stable operation identities and receipts allow retry or status lookup without repeating effects;
reusing an identity with different input is a conflict. Receipt access itself remains authorized.

File workflows need authorized transfer and retrieval contracts, size/type limits, integrity checks,
and clear completion state. They must work without passing server filesystem paths or unbounded
binary payloads through a model conversation. References remain tenant-scoped and authorized;
an export or attachment identifier is not a public sharing credential.

Stored notes, supplier names, documents and imported material are untrusted data, including when
returned by tools. Such content cannot grant roles, establish approval, or instruct the server to
contact arbitrary destinations. Any later external-fetch capability needs its own explicit contract.

## Autonomy, approval, and accountability

Recommended policy: assigned roles grant standing authority for their ordinary business operations.
Do not require an additional human confirmation for every descriptive write. Workbench defines
explicit approval requirements for operations whose business policy requires them, consistently
across interfaces. Which operations need an independent approval is still a product decision.

When approval is required, enforce it on the server against the exact operation, principal, tenant,
input, relevant record versions, and expiry. An agent-supplied `approved: true` value, a role name,
or a tool annotation cannot prove a human approved something. Material changes invalidate approval.
The agent can prepare work and inspect approval status but cannot act as its own human approver.
The human approval action remains outside the service-principal authority boundary.

Attribute business changes to the authenticated principal and record operation identity, affected
records, time, outcome, and relevant approval reference. Retain human grant/revocation events as
separate administration history. Existing immutable financial records remain immutable.
Operational logs minimize private payloads; evidence retention and access must be deliberately
defined, rather than logging every prompt, document, or tool argument by default.

## Motivating scenario: supplier migration

A user asks an agent to merge suppliers from another system into Workbench. The agent reads
existing suppliers, presents proposed matches and field changes, resolves ambiguous identities,
then executes the authorized changes and retrieves a reconciliation report.

The completed GemInv migration illustrates the desired outcome: 34 source suppliers corresponded
to 11 preserved Workbench suppliers and 23 additions, with one missing social reference added.
The future interface should make that work possible without navigating individual editor forms.
Names alone must not silently establish identity. Preserve richer existing values unless the
requested merge policy authorizes replacement; preserve existing supplier identifiers and links.
Retrying an interrupted import must not create another set of suppliers.

This is a source-neutral workflow. Reading a local PostgreSQL backup belongs to the agent's source
integration or a separately defined importer, not to an MCP command that executes arbitrary SQL.
A preview/apply merge feature is new shared application behavior if Workbench lacks it; it must
not become an MCP-only implementation of business matching rules.

## Acceptance principles

1. Every shipped web business workflow has an explicit parity classification and permission mapping.
2. A principal with a predefined read role can perform its authorized reads and cannot write.
3. Adding/removing predefined roles changes effective access without introducing custom roles.
4. Tenant substitution, record-ID substitution, excluded tool invocation, and attempts to administer
   identities or principals fail even when made directly, outside normal tool discovery.
5. Web and MCP produce equivalent business outcomes and domain failures for equivalent authorized
   operations; actor attribution correctly differs by identity type.
6. Role removal, credential revocation, and principal disablement reject subsequent operations
   across replicas, including execution of prepared work. In-flight behavior is explicitly tested.
7. Concurrent edits produce actionable conflicts. Uncertain retries have one business effect and
   an authoritative retrievable result, including after later edits where the domain supports it.
8. Required approvals cannot be forged, transferred between principals, or reused for changed work.
9. Documents, exports, and batch results remain private, bounded, and recoverable after interruption.
10. At least two representative MCP clients demonstrate the chosen authentication and core workflow
    contracts against hosted and self-hosted configurations before claiming interoperability.

## Decisions to close before implementation planning

- Approve the initial predefined role catalog and default setup preset, including the separation
  of business configuration from human-only tenant/identity administration.
- Select supported clients and credential flows, expiration/rotation policy, and the workload
  authentication mechanism; prove compatibility instead of assuming user OAuth equals principal access.
- Classify which consequential operations require independent human approval and how an authorized
  human supplies it without granting the principal administration rights.
- Define activity/audit visibility and retention, role-expansion policy, and ownership review when
  the human who provisioned an integration leaves or loses administrative authority.
- Choose the initial delivery slice and parity inventory. Supplier management is a useful proving
  workflow, while identity/authorization foundations must support the full product destination.

These are focused follow-on design decisions, not authorization to implement the server or activate
an integration. This product spec intentionally does not choose database migrations, SDK versions,
individual tool schemas, or an implementation task sequence.

## Milestones and future agent handoffs

The [milestones and user stories](2026-09-28-mcp-milestones-and-stories.md) define product outcomes,
acceptance evidence, prerequisites, and parallel delivery tracks. Each story undergoes its own
brainstorming, design, implementation, and validation before delivery; the roadmap is not an
implementation plan or blanket approval to begin development.

## Evidence and references

- Current application contracts: [Architecture](../ARCHITECTURE.md),
  [API lifecycle](../api-lifecycle.md), and [database principals](../operations/database-principals.md).
- Current actor/permission anchors: `src/Workbench.Server/Authorization/IRequestActor.cs`,
  `WorkbenchPermissions.cs`, and `src/Workbench.Server/Purchasing/SupplierEndpoints.cs`.
- [MCP authorization](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization)
  defines HTTP authorization and resource-bound token requirements; this does not establish client
  support for a particular service-principal credential flow.
- [MCP tools](https://modelcontextprotocol.io/specification/2026-07-28/server/tools) describes the
  protocol tool surface. Product permissions, approval policy, and domain consistency remain
  Workbench responsibilities. Protocol references were checked during design on 2026-09-28;
  implementation must select and verify its supported protocol/SDK/client combination.
