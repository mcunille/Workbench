# PO-06: private invoice attachments on purchase orders

**Status:** Implemented attachment scope. The owner clarified that invoices are
one or more PDF attachments, like GemInv, and instructed continuation with that direction.

## Scope and user outcome

Extend an ordered purchase with multiple private invoice files. The owner can select several
documents, label them, upload, download, rename and explicitly remove them without entering
invoice accounting data first. Uploading files does not alter agreed contents, estimates,
payments, inventory or order revision history. Existing acquisition documents remain unchanged.

This is the attachment portion of [PO-06](2026-09-11-purchase-orders-and-purchase-finances.md).
Structured invoice numbers/dates/terms/amounts, invoice correction events, amount comparisons and
duplicate supplier-reference warnings are deferred. File count is not invoice count: several
files may describe one invoice, and a PDF may contain several invoices. No extraction is implied.
The prior proposal for a structured invoice subsystem is superseded by this narrower scope.

Invoices belong directly to an ordered PO. Draft attachments, receipt/payment workflows,
line matching, generalized document-owner frameworks and new storage providers are outside scope.
GemInv's purchase-order attachment panel is the interaction reference; Workbench retains its
own validation, permissions, recovery and retention contracts.

## Interface — impeccable, Operate mode

Keep the existing Tanzanite ordered view, toolbar, items, charges, estimates and Details disclosures.
Below the estimates, add **Invoice files** with **Add invoice files**. Compact rows show a label,
validated format, size and upload date, with Download, Rename and Remove actions. Empty content
invites supplier invoices or supporting purchase paperwork. No summary tiles or new global tokens.

An inline uploader accepts multiple files and offers an editable label for each, initialized from
the filename's basename. Labels are display text only; paths never become storage keys. Upload
sequentially to respect server admission and obtain each new PO version. Show per-file saved,
pending or failed state; stop on failure and resume without re-uploading successful files. Keep
unsent files and labels in memory across recoverable errors. Do not invent progress percentages.

Do not permit a new command to replace an uncertain one. Check operation status and retry its
exact payload/UUID first. Once success is confirmed, a failed list refresh retries only the read.
A definitive conflict offers an explicit review of current documents before a new versioned
request; retain the unsent selection. File validation errors allow correction/removal of the
affected queued file. Cancel/discard cannot imply that already uploaded files were removed.

Removal confirms the specific label and explains that application access ends immediately while
retained bytes follow policy. Rename changes only the label. Downloads are independent reads and
can be retried without creating records. Recovery-unavailable files remain listed distinctly.

Wrap long labels, stack actions on narrow screens, preserve keyboard focus and name every control.
Loading, empty, failure, validation, uncertain and saved states have distinct accessible feedback.
Guard navigation with unsaved/uncertain work, prevent amendment/history transitions from discarding
it, and clear private file/draft/list state on authentication loss. Only saved files survive reload.

## Documents and storage

Reuse the existing document validator and attachment lifecycle: PDF, JPEG, PNG and WebP, maximum
10 MiB per file, 20 current or pending files per PO. Upload is one bounded multipart request per
file. Count durable pending reservations toward capacity. Retain the existing restricted PDF and
image parsing policies, admission limit, deadlines and safe downloads; do not promise acceptance
of every PDF or antivirus certification. Source bytes may contain embedded metadata.

Require trimmed labels of 1–200 code units. The UI provides a filename-derived default so the
owner need not type one. Do not store client paths or rely on MIME/filename for content validation.
Content is immutable: add another file and explicitly remove the old one to replace it. Removal
revokes application access and uses the existing seven-day retention and holds, not immediate
physical erasure. Persist document metadata and command evidence for recovery and retries.

Authenticate and resolve tenant/PO/document membership before returning metadata or bytes.
Downloads use application attachment responses, safe generated filenames, private no-store and
no-sniff headers; verify digest and length before a successful response. Never return provider paths
or public URLs. Purchase documents participate in existing paired SQL/blob backup and recovery.

## Persistence, concurrency and API

Add purchasing-owned document metadata and durable document-operation evidence with tenant-qualified
foreign keys to the PO and immutable storage identity. Apply tenant RLS filter/block predicates,
restricted command procedures and runtime direct-DML denial. Mutations require an ordered PO;
missing or foreign identifiers return indistinguishable 404s. Use existing membership authority
and antiforgery protection. Do not create synthetic acquisitions or refactor their ownership.

Use PO-then-document lock ordering. Every change checks PO rowversion; rename/removal additionally
checks document rowversion. Successful changes advance PO rowversion without advancing its agreed
content revision. Serialize exact request UUIDs across replicas; record immutable normalized
payload evidence including versions and, for upload, length/digest. Exact completed retries return
the original result after authorization; changed payload reuse conflicts and cannot revive removals.

Prepare an upload in a SQL transaction, publish bytes outside parent locks, then finalize after
rechecking versions and context. Partial publication is never a visible saved document. A stale
publication loser retires bytes using the current retention lifecycle. Ambiguous storage failures
retain evidence for exact retry/reconciliation; never delete ambiguous bytes to unblock work.

Routes under `/api/beta/purchase-orders/{id}/documents`:

- GET: documents plus current `orderVersion`, read from a consistent database view.
- POST: multipart `file`, `label`, `requestId`, `expectedOrderVersion`.
- PUT/DELETE `/{documentId}`: request ID, expected order/document versions, label (null on removal).
- GET `/operations/{requestId}`: authenticated durable operation status.
- GET `/{documentId}/download`: private validated source bytes.

Document responses contain ID, label, media type, validated extension, byte length, creation time,
version and unavailable status. Operation responses contain request ID, state, document ID and
result order version. Use existing Problem Details/status semantics: validation 400/413/415/422,
inaccessible 404, stale/request/capacity 409, recovery-unavailable 410, retryable admission/storage
503. Generate the client from OpenAPI. Existing purchase/draft payloads remain unchanged.

The release uses an additive document migration followed by an authority-guard migration. The
second forward migration preserves the first migration already applied to the retained local
preview; that applied history must not be rewritten. Preserve base migrations and retained
databases. A read-only check on 2026-09-20 of this checkout's retained preview
(`dev-70c8b9b310fa437ba376994b1911f28d`) confirmed both
`20260918061646_AddPurchaseOrderDocuments` and
`20260918063409_HardenPurchaseOrderDocumentAuthority` in `__EFMigrationsHistory`.
Keeping these applied migrations follows the repository's prohibition on rewriting retained
history; no separate owner-approved consolidation exception is claimed. Update
readiness/provisioning/permission checks. Verify clean
creation and base upgrade preserving existing records and receipts. Guard destructive rollback
once document evidence exists; paired SQL/blob restoration is the recovery path.

## Alternatives and rationale

Structured invoice entry before upload adds work the owner did not request; attach files directly.
A separate invoice entity would imply grouping and financial policy; defer it until needed.
Copying GemInv's storage implementation would change established safety/operations contracts;
reuse Workbench primitives with a purchasing-specific adapter instead.

## Acceptance and delivery

1. Upload two PDFs to an ordered purchase, reload and download their exact bytes. Rename one,
   cancel then confirm removal, and verify only the confirmed removal revokes access.
2. Mixed-success uploads preserve saved results and pending input. Lost responses retry exact
   identity; confirmed writes with failed reads never upload again. Competing changes require
   review; document commands do not silently authorize edits against stale PO contents.
3. Reject draft/foreign access, bad labels, invalid files, oversized requests and capacity overflow.
   Real SQL tests cover direct-write denial, tenant boundaries and independent-connection races.
4. Verify interrupted publication and paired recovery; unavailable files retain useful metadata.
5. TDD focused rules/API/component behavior with Gherkin comments; run targeted mutation checks
   or state unavailable tooling. Exercise persisted preview workflow, mobile/desktop, light/dark,
   keyboard, 320px and enlarged text. Keep visual evidence outside Git.
6. Complete current-source verification, container smoke and independent review, update living
   documentation, commit and open a ready-for-review PR. No merge or production operation.
