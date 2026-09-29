# BK-07: durable financial evidence

**Status:** Design direction approved; this written specification awaits review. No BK-07
runtime implementation or verification is claimed. Written-spec approval precedes the
implementation plan and its separate review/execution-method handoff.

Parent: [PO-07 bookkeeping prerequisites](2026-09-20-po-07-deposits-and-payments.md).
Baseline: `ddb2101a887bd33f43f91c00ef07a3fbc0698de3`, including merged BK-01–06 and
schema `20260928071548_AddSupplierOpenItems`.

## Outcome and scope

Give the owner durable, private supporting evidence for posted financial sources. Corrections,
permission changes, ordinary PO-file removal, cleanup, and supported recovery must not erase
the audit trail. Financial identity and retention survive even when the original bytes are
missing; reporting must distinguish preserved metadata from available content.

Deliver database-enforced evidence links and retention, integration with existing internal
posting/correction paths, authorized readback, explicit disposal after expiry, and paired
SQL/file recovery coverage. Preserve ordinary purchasing and unposted file removal without
requiring accounting setup. Keep `BookkeepingAvailable` false. Do not enable public bill/payment
posting, financial mutation grants, BK-08 sources, bank reconciliation, or production bookkeeping.
Evidence-management operations do not post or change money.

Reuse existing supplier/account/mapping snapshots and private immutable storage revisions.
Add missing protections rather than rebuilding the journal or copying files to a parallel archive.
Application-only guards cannot protect SQL callers and concurrent cleanup; a second evidence
archive would duplicate publication, access, backup, and recovery infrastructure.

## Current integration points

- `SupplierBillReviewCommands.ValidateBillEvidence` validates live document identity and
  captures revision/digest/length/label. Review alone does not reserve evidence indefinitely;
  posting must revalidate and acquire retention atomically.
- `SupplierBillPosting`, payment commands, recognition, and correction participants own
  source authenticity. The generic journal kernel must not accept caller-invented document
  links as proof of an authentic business source.
- `PurchaseOrderDocumentSchema` coordinates two-phase file operations under the PO lock.
  Removal currently marks the document removed and schedules attachment cleanup after seven days.
- `WorkProcessor.DeleteAsync` locks the attachment and checks `Held` before deleting bytes.
  Keep this existing protection and add authoritative financial-retention checks; a boolean
  alone cannot explain multiple source links, policy versions, or expiry.
- `FileRecovery`, `FileRecoverySchema`, manifests, and recovery dispositions distinguish SQL
  identity from recovered bytes. Extend these contracts rather than weakening pending-recovery
  gates or treating a missing document as recovered.

## Authoritative records and invariants

Use tenant-qualified keys and foreign keys, RLS, denied direct runtime DML, and restricted
commands. Store conceptual records with these responsibilities:

1. **Evidence set:** immutable typed source identity/revision or evidence group, source owner,
   supplier/PO identity where applicable, actor and UTC recorded instant. Preserve the source's
   original snapshots and hashes. Zero-value posted sources can own evidence without a journal.
2. **Document link:** source evidence set, document/attachment/revision IDs, SHA-256, length,
   original label/media metadata, policy configuration version, retention years/rationale,
   anchor instant and minimum retention deadline (null means indefinite). Identity and original
   metadata never change when the live supplier, account, mapping, or document label changes.
3. **Evidence addition:** immutable, reasoned supplement or replacement relationship linking a
   newly uploaded revision to a source and, for replacement, the previous evidence link. This
   appends history; it never edits the source snapshot or discards the original link.
4. **Disposal receipt:** immutable actor, request ID, canonical request/hash, reason, complete
   affected evidence-link identities, authorization time, and resulting document-operation ID.
   Record physical cleanup separately; authorization is not proof that bytes were deleted.

Each evidence-bearing posted source must have authenticated links or an explicit missing-evidence
reason. Source-specific validation remains responsible for the allowed shape and business identity.
An omitted document is not an empty available document. Supplementing a metadata-only source
preserves the original missing-evidence declaration and adds new evidence history.

Reuse source-owned bill and payment evidence relationships; do not duplicate their financial
snapshots in a second editable model. Recognition and correction sources must expose explicit
typed participation, including their inherited evidence relationships. Arbitrary IDs or free-text
JSON containing document-like fields cannot acquire source authority. Future BK-08/BK-12 sources
must implement the same contract before they can post with documents.

## Retention policy

Use existing `RetentionYears` (1–1000) and `RetentionRationale`. These become operational
document-retention settings, not jurisdiction-derived legal rules or a compliance certification.

- At initial acquisition, anchor retention to the later of the source posting date at midnight
  UTC and the link's SQL-recorded UTC instant. For sources without a journal, use the source's
  posting date. Supplements use their own recorded instant and the original source posting date.
- Freeze the policy version, years, rationale, anchor and computed deadline with the link.
  Add calendar years, clamping February 29 to February 28 when necessary. Reject an unrepresentable
  deadline before any posting or link writes; do not wrap or silently truncate it.
- A null duration means indefinite protection and unresolved configuration. Choosing a duration
  later applies to new links; it does not retrospectively release indefinite protection.
- Configuration edits affect future links only. They cannot shorten or rewrite any existing
  deadline. This increment does not introduce a bulk retrospective policy-change command.
- All links to an attachment protect its revisions. Disposal requires every financial link to
  have a finite deadline at or before the database's current UTC instant. A later new link can
  extend protection. Existing independent `Held` protection always blocks disposal.
- Reversal, replacement, settlement, account archival and permission loss do not release links.
  Expiry merely makes explicit disposal eligible; there is no automatic expiry-deletion job.
- Journals, source snapshots, links, policy evidence and disposal receipts are retained
  permanently by this feature. Only eligible document bytes and ordinary live-file visibility
  may be removed through the controlled disposal path.

Example: a source posted September 1 but linked September 28, 2026 at 18:00 UTC with seven years
of retention cannot be disposed of before September 28, 2033 at 18:00 UTC. A later correction
does not remove that protection. A second link made in 2028 may protect the attachment longer.

## Atomic posting, removal and cleanup

Uploads finish through the current private publication pipeline before linking. Under the
source transaction, authenticate source/tenant/actor, lock and revalidate the exact live document,
attachment and revision, verify matching stored digest/length and recovery availability, acquire
the links, and commit source/journal/receipt together. A failed posting leaves the uploaded file
unposted and creates neither a durable hold nor partial financial state. Request replay returns
the original result and does not extend deadlines or duplicate links.

Serialize source commands and PO file changes on the existing PO coordination lock. Within that
boundary use stable ID order and attachment-before-revision ordering for evidence and cleanup;
retain existing accounting/source coordination locks in their established order. Cleanup must
not acquire PO/accounting locks after holding an attachment lock. Resolve the existing validation
query's optimizer-dependent multi-table lock acquisition into explicit ordered reads where needed.
Prove the final order with real competing connections, including multiple-document transactions.

Ordinary removal rejects any financially linked document, including expired links, with a
recoverable conflict explaining the retention/disposal route. Check both operation preparation
and completion: an operation prepared before posting cannot later hide or delete held evidence.
Posting that loses to completed removal rejects the unavailable revision. Renaming an active
document remains possible; the historical evidence label stays unchanged.

Physical cleanup checks authoritative link/deadline/disposal state and independent holds while
holding the attachment lock through external deletion. Stale work leases and pre-migration work
items cannot bypass this check. A lost cleanup acknowledgement can retry deletion idempotently;
after authorized removal, no new source can link that unavailable document. All generic attachment
deletion procedures and worker completion/state-transition procedures enforce the same predicate.
Do not rely solely on a changed application binary to protect an older worker or SQL command.

## Authority, readback and explicit disposal

Retention never grants document access. Preserve current authenticated tenant and PO-document
access checks and source-specific accounting permissions. A new evidence read requires both
source-read authority and the underlying document-read authority. Existing ordinary PO document
access does not newly grant accounting snapshot access. Check current session/membership/roles
on each request; clear private client state on access loss and keep responses non-cacheable.

Expose historical link identity and retention status through authorized source readback. Keep
availability separate: no-document-declared, available, missing/corrupt after recovery, and
disposed are distinct outcomes. Derive current availability without rewriting original snapshots.
Downloads verify length/digest, return a specific unavailable outcome for known missing/corrupt
content, and never return a silently substituted file. Provider outages remain retryable failures.

Provide an explicit retained-document disposal action separate from ordinary Remove, using
`AccountingConfigurationManage` plus current PO-document mutation authority. This uses an existing
assignable permission and does not create a blanket financial-posting permission. The confirmation
names the document and warns that its bytes become eligible for permanent removal while accounting
metadata remains. Require a nonblank reason of at most 2,000 characters.

The disposal request carries request ID, expected PO/document versions and expected evidence-set
version. In one restricted SQL transaction, recheck session/permissions, all link deadlines,
independent holds, recovery readiness and versions; record the receipt and remove the live document
through the existing storage workflow. Schedule physical cleanup no earlier than the existing
seven-day removal grace. A new link or hold racing disposal either wins and blocks disposal or
loses to removal and cannot attach. Recheck independent holds at physical cleanup too.

Use a dedicated `POST /api/beta/purchase-orders/{id}/documents/{documentId}/retention-disposals`
contract with antiforgery and existing Problem Details conventions: validation 400, authority 403,
inaccessible resource 404, stale versions/retention conflict 409, retryable infrastructure 503.
Return the durable operation receipt and support same-request outcome lookup after a lost response.
Reject changed-payload reuse. Ordinary document responses add retention status and disposition
eligibility without disclosing inaccessible financial source details. Client-generated contracts
must come from OpenAPI. Existing Tanzanite components own status, confirmation and focus behavior.

Evidence supplementation/replacement commands remain internal source-owned operations with the
source's existing mutation permission and authenticated session. No public bill/payment evidence
editor or new financial write grants ship in BK-07. Their SQL tests use disposable authorized adapters.

## Recovery, backups and migration

Supported SQL/file recovery preserves links, snapshots, policy deadlines and disposal receipts.
Recovery inventories and fingerprints include retention/disposal evidence needed to prevent a
stale report accepting changed protection. Inventory every protected revision even when a legacy
document was removed or its attachment entered a deletion grace state. Such rows cannot be treated
as orphans. Recovery disposition records missing/corrupt bytes without dropping financial links.
Unverified bytes never become available merely because their ID exists at the target provider.

Rebinding can change a provider location, not a revision's identity or digest. Backup manifests
include held revisions; new backups retain a recoverable copy of currently protected content.
Backup-set expiry continues to follow its separate recovery-window policy: financial retention
protects authoritative content, not every historical backup forever. Document this distinction,
test recovery from a supported retained set, and preserve provider immutability/legal-hold behavior.
After legitimate disposal, an older backup may contain bytes until that backup expires. Recovery
must reapply the restored SQL disposition before users/workers resume; it must not present disposed
bytes as active evidence or promise physical erasure from all backups.

Ship one forward migration after `20260928071548_AddSupplierOpenItems`. Preserve all merged
migrations and financial hashes. Backfill links from authenticated existing posted bill/payment
and source evidence only, including reversed and zero-value sources; exclude draft/review-only
records. Use indefinite protection for legacy evidence because the original retention policy was
not enforced. Preserve recorded historical metadata rather than presenting today's mutable names
as original snapshots. Malformed or contradictory source/revision identities block upgrade with
actionable diagnostics, without partial writes.

Existing missing or already removed evidence remains linked and explicitly unavailable. Prevent
pending cleanup, retain any surviving bytes, and do not falsely reactivate a removed document.
Recovery inspection establishes byte availability before declaring upgraded evidence recovered.
Do not perform a financial backfill, recompute journal hashes, delete preview data or reset migration
history. Update schema markers, readiness, provisioning/permissions, model snapshot and supported
restore tooling. Block incompatible old writers/workers from bypassing holds. Destructive down
migration is prohibited; use forward correction or verified coordinated recovery.

## Verification and acceptance

Use TDD with Gherkin comments and the cheapest sufficient boundary. SQL authority, real races and
paired recovery require integration coverage; UI formatting and confirmation need focused client
tests. Each acceptance case owns a distinct regression:

1. Bill/payment/recognition evidence commits with source/journal/receipt, including zero-value
   sources; failed posting rolls back links. Replay creates one result without changing deadlines.
2. Cross-tenant revisions, mismatched document/attachment/digest, removed/pending/recovery-missing
   files and fabricated source ownership fail without partial writes. Missing-evidence reason works.
3. Supplier/account/mapping edits and document renames cannot change historical snapshots.
   Supplements/replacements preserve original evidence; reversals and full settlement retain holds.
4. Frozen finite deadlines, leap-day handling, overflow, unset policy, later policy edits, multiple
   links and independent holds produce the stated eligibility at exact boundary instants.
5. Race posting against removal preparation/completion and cleanup using independent SQL
   connections. Include stale worker leases and multi-document acquisition; no held bytes are lost.
6. Disposal requires both authorities, reason and current versions; early, indefinite and held
   disposal fails. Replay after expiry/removal works; changed-payload replay fails. Permission loss
   denies fresh reads/downloads/writes without deleting evidence or leaking cross-tenant identities.
7. Direct runtime DML, generic deletion calls and old-procedure/worker paths cannot bypass holds.
   Cleanup retries preserve truth after provider failure or a lost acknowledgement.
8. Backup/restore/rebinding retains all source hashes and evidence links; missing/corrupt held files
   are visible, protected files are not orphans, and disposed bytes are not resurrected as active.
   Altering retention state invalidates a previously prepared recovery report.
9. Fresh installation and upgrade from the merged BK-06 schema preserve existing financial data,
   including removed legacy evidence and pending cleanup. Test migration guards and rollback refusal.
10. Ordinary unposted files retain their existing lifecycle without accounting setup. In the isolated
    preview, inspect retained status, blocked ordinary removal, disposal confirmation and permission
    loss with non-sensitive synthetic evidence; exercise narrow viewport and keyboard interaction.

Run targeted mutation probes against hold checks, expiry comparisons, permission checks, completion
revalidation and recovery inventory inclusion. Use available tooling and disclose unsupported mutation
coverage rather than claiming a score. Run current-source `./scripts/verify.ps1` and the separate
`./scripts/smoke-container.ps1`, then inspect the refreshed preview from `./scripts/dev-up.ps1`.
Independent internal review must cover the complete change, especially SQL/storage transaction gaps.
Report local URLs, exact gate outcomes and limitations; capture non-sensitive visual PR evidence.
Deliver a ready-for-review PR after implementation and verification. Merge and production activation
remain separately authorized.

## Design review record

The owner approved the SQL-enforced direction, frozen retention, explicit disposal, correction
preservation and recovery/access boundaries in chat. This document makes their detailed semantics
reviewable, including the retention anchor, legacy backfill, indefinite-policy behavior and disposal
authority. Those details are proposed until this written specification is approved.
