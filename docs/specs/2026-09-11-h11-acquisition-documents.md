# Keep acquisition paperwork

**Status: Implemented — owner approved implementation on 2026-09-11.**

Implements [H11 / #76](https://github.com/mcunille/Workbench/issues/76) within scenario
[#73](https://github.com/mcunille/Workbench/issues/73). The accepted
[acquisition design](2026-09-09-acquisition-context.md#h11-and-h12-extension-boundaries)
sets the initial formats, limits, privacy and retention boundaries. This proposal supplies
the focused implementation contract required there.

## User experience and scope

Show a Documents section in the acquisition context reached from an item. Documents belong to
the acquisition and are shared by its linked pieces; there are no item-specific document links.
Explain this when adding or removing paperwork. A report is collector-supplied evidence, not
verification of authenticity. Preserve existing item entry, acquisition text, search and navigation.

Require a plain-text label, trimmed, 1–200 characters. List label, validated format, exact stored
byte size, uploaded date and a download action. Use server-generated safe download filenames
with a document identifier and validated extension; do not retain local paths or trust client MIME
types. Labels need not be unique. Do not embed PDFs or offer public blob URLs.

Support adding, renaming and explicitly removing a document. File content is immutable: correct
content by adding the corrected document and removing the mistaken one. Removal immediately
revokes application access and uses existing seven-day retention and holds; it does not promise
immediate physical erasure. At the limit, explain that removal is needed before another upload.

Read access survives item archive and restore. Mutations must originate through a currently active
linked item. An archived item shows read-only documents even when other linked items are active.
Unlinking an item does not delete documents; H10 owns link management. H12 package changes, financial
fields, public sharing, imports, acquisition deletion and a document history UI are outside H11.

## Ingestion and delivery policy

- Accept PDF, JPEG, PNG and WebP only, up to 10 MiB input and 20 current documents per acquisition.
  Count reservations for pending uploads toward capacity so concurrent uploads cannot exceed it.
  Limit multipart overhead to 64 KiB, one file, and bounded fields, including chunked requests.
- Independently validate signatures and structure on the server. Decode images with the existing
  restricted image tooling, allowing only a single image, at most 40 million pixels and 12,000
  pixels per axis. Preserve validated source bytes so paperwork remains downloadable as supplied;
  do not claim that metadata was removed. Explain that files may contain embedded metadata.
- Parse PDFs with a maintained, license-compatible library and reject malformed or encrypted files,
  embedded files, JavaScript, launch actions and external action targets. Reject unsupported parsing
  features rather than treating them as successful validation. Cap PDFs at 200 pages. Library
  selection must demonstrate these rejection cases before integration; regex checks are insufficient.
- This is format and active-content validation, not antivirus certification. No external scanning
  service receives private files. Parser suitability is an implementation qualification gate; if no
  suitable dependency can meet this policy, return with the concrete tradeoff before changing it.
- Use one validation job per process, bounded input buffers, a two-minute request deadline, and the
  existing native-image resource policy. Reject excess admission with retryable feedback. Cooperative
  cancellation is not a hard isolation boundary for native/parser execution; deployment memory limits
  remain necessary. Include adversarial resource fixtures in qualification.
- Authenticate and resolve the item/acquisition/document relationship before returning metadata or
  bytes. Download through the application with attachment disposition, validated Content-Type,
  Content-Length, no-sniff and private no-store. Verify the complete bounded blob digest before
  beginning a successful response, so known corrupt or missing files produce an actionable error.

## Storage, concurrency and retries

Add tenant-owned acquisition-document metadata and command evidence, linked with tenant-qualified
FKs to acquisition and immutable storage attachment identity. Persist label, validated type, stored
length/digest, server timestamps and rowversion. Keep storage provenance in the existing provider
model. All new tables receive tenant RLS filter/block predicates; restricted SQL commands own writes,
and runtime direct DML is denied. Update readiness and provisioning assertions.

Use item-then-acquisition lock ordering, consistent with acquisition commands. Upload preparation
checks active linkage, expected item/acquisition versions and capacity, then creates a durable
reservation, immutable operation identity and pending revision. Serialize identical operations across
replicas. Publish provider bytes before a final SQL transaction exposes the document. Recheck linkage,
archive state and versions at finalization; increment item and acquisition versions on successful
document changes. A stale command never silently adopts a newer version.

Each mutation carries a tenant-scoped request UUID and immutable normalized payload evidence.
For upload, bind evidence to item/acquisition, label, input length and SHA-256, and the original
versions. Identical successful retries return the recorded outcome without duplicating or reviving
a removed document. Reusing the UUID for another payload conflicts. Rename/removal use document
version as well as item/acquisition versions and preserve equivalent replay evidence.

An interrupted operation remains pending, never visible as a saved document. Preserve its exact
request for retry and provide an authenticated operation-status read. Definitive rejection releases
capacity; ambiguous provider outcomes retain reconciliation evidence and use existing offline
reconciliation and retention rules. Do not delete ambiguous bytes or invent success to unblock retry.
An operation that loses a race after publication retires its bytes through the retention lifecycle.
Committed replay can be read after archive, but cannot bypass current tenant/link authorization.

## API and client states

Use item-scoped routes beneath `/api/items/{itemId}/acquisition/{acquisitionId}/documents`:
GET lists current documents; POST uploads multipart content and command fields; PUT `/{documentId}`
renames; DELETE `/{documentId}` removes with checked command fields; GET `/{documentId}/download`
delivers content. GET `/operations/{requestId}` resolves uncertain commands within the same context.
Generate client contracts. Reuse authentication, antiforgery and Problem Details conventions.

Missing or foreign context/identifiers return indistinguishable 404s. Invalid files/labels return
actionable validation errors; oversized requests return 413; stale state or request-ID reuse returns
409; unavailable storage returns 503 with retry guidance. A known recovery-unavailable document
remains listed with an unavailable state, distinct from an empty list. Never disclose provider paths.

Separate loading, empty, load failure, selected-file draft, pending, uncertain, conflict and confirmed
success states. Keep file, label and request identity in memory across failures and appearance changes;
warn before discarding and clear private state on authentication loss. No persisted browser drafts.
Resolve uncertain outcomes before accepting another submission. Show upload activity without invented
progress percentages. Download retry is read-only and never creates an attachment.

Use existing visual components, keyboard controls, visible focus and live feedback. Preserve focus
after cancel/completion and focus actionable errors. Stack actions and wrap labels at 320px; exercise
mobile/desktop, both appearances, reduced motion and reduced transparency.

## Verification and migration

Write focused failing behavior tests before implementation. Cover valid formats, label boundaries,
misleading MIME/extensions, encrypted/active/malformed PDFs, oversized/complex images, request bounds,
capacity, unavailable/corrupt storage, lost responses and identical/different retries. Test API and
direct SQL tenant isolation, foreign links, runtime write denial and archived read/write behavior.
Use independent SQL connections for identical uploads, last-slot competition, rename/removal and
archive/link-change races; assert no duplicate success, leaked capacity or visible partial document.

Deliver one coherent forward migration, preserving base migrations and retained environments. Verify
fresh creation and upgrade from PR-base schema, retained item/photo/acquisition data, new permissions,
readiness and a destructive-down guard when document data exists. Verify paired SQL/blob backup and
recovery with documents and explicit missing-file disposition. No production operation is authorized.

Run affected mutation checks and report exclusions or unavailable tooling. Run CONTRIBUTING's full
verification and container gates, then inspect the current-source isolated preview. Record a narrated
Playwright walkthrough with non-sensitive documents covering mobile/desktop and a failure/retry path.
Keep media outside Git; attach using supported GitHub tooling. Deliver a ready-for-review PR only
after implementation and verification, with honest coverage limits.

## Alternatives

Per-item attachments duplicate shared paperwork; acquisition ownership matches the accepted model.
In-place content replacement adds hidden revision semantics; explicit add/remove keeps correction clear.
Inline PDF viewing increases active-content exposure; attachment delivery meets retrieval needs.
Rasterizing PDFs changes original evidence and adds rendering dependencies; validated original downloads
preserve the source, with the disclosed malware and embedded-metadata limitations above.

## Qualified PDF subset

The implementation pins PdfPig 0.1.16 and supplements its normalized object model with bounded
original-syntax checks for duplicate dictionary keys. Plain PDFs and JPEG scan PDFs are qualified.
Compressed object/cross-reference streams, inline images, stream decode parameters/predictors,
forms, actions, external references and unqualified codecs are rejected with re-export guidance.
See [provider operations](../operations/blob-and-service-providers.md) for the exact supported
encodings, resource bounds and qualification limits. This does not promise acceptance of every
otherwise valid PDF or certify files free of malware.
