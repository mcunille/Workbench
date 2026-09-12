# Collection guide

This is the owner of current collection behavior. [Architecture](ARCHITECTURE.md) owns technical
contracts, [export formats](collection-export.md) owns CSV/ZIP, and [dated specs](specs/README.md)
retain design decisions. [Walkthroughs](demos/README.md) link evidence and reproduction procedures;
those checks do not establish collector usability or production acceptance.

## Save and find a piece

After signing in to an existing tenant, save one individually tracked gemstone or jewelry piece
with a name and optional plain-text notes and storage location. Names need not be unique. Browse
Grid or List and reopen the saved record across sessions. Unsaved work is protected, and retrying
an uncertain creation does not create another item.

Add, replace or remove one private photograph on a saved item. Preview browser-prepared JPEG, PNG
or WebP before uploading; the original stays on the device. A failed upload preserves the existing
photo. Collection cards, list rows and details show the current photo or a neutral placeholder.
Removal follows storage retention rules and does not promise immediate physical erasure.

Search names, notes and locations with a literal phrase across the entire active collection,
including unloaded pages. Returning from details preserves the query, Grid/List view and position
inside the signed-in app. Reload or authentication loss resets private navigation state.

Sources may be up to 20 MiB and 40 megapixels, with at most 16,384 pixels per axis. Preparation
handles orientation/color and resizes locally. If the browser cannot prepare the image, it shows
an actionable error; it never uploads the original as a fallback. Convert HEIC/HEIF, RAW, animated
or multi-picture images to a supported still-image format before selecting them.

## Correct, archive and recover

Edit the name, notes and location without changing item identity. Saves check the version that was
opened; concurrent changes require explicit reconciliation. Failed saves retain the draft for retry,
and successful edits refresh search results. Photographs and descriptive edits share conflict checks.

Confirm Archive to remove a piece from active browsing and search. Archive is separate from a sale,
disposal or ownership change. Existing authorized links retain read-only details and photographs.
Open the separate searchable Archive to restore the same item, identity and photograph to Collection.
Archive and Restore check the saved version; a stale retry cannot reverse a later change. There is
no permanent-delete workflow.

## Record origin and take a copy

Saved items support optional acquisition context: method, free-text source, unknown or partial
acquired date, and collector-recorded provenance notes. Corrections check versions and retain drafts
for conflict recovery. One acquisition can describe several independently recorded pieces;
each piece has at most one current acquisition. Open View acquisition to see associated pieces,
connect an existing piece or record a new one, and return through the collection without losing
its query, view or position. New-item entry saves the piece first; a failed connection save
retains that item and retries only the relationship.

Use Change acquisition or Remove connection to correct a mistaken association. Review the
current and intended context before confirming. Replacing a connection is atomic, and both
acquisitions and the piece remain saved. An acquisition with no connected pieces remains
findable in the acquisition picker. Shared context corrections apply to every associated
piece, including archived pieces; there are no separate copies to diverge.

Archived item details and acquisition views opened from them are read-only. Ordinary associated
piece browsing hides archived records unless Show archived pieces is selected. Archive and
restore preserve the relationship. After an uncertain connection save, retry the original
request or review current state; matching saved values do not prove which request succeeded.
Conflicts require deliberate review before saving with fresh versions.

The Documents section retains labeled acquisition paperwork shared by all connected pieces.
Add a PDF, JPEG, PNG or WebP up to 10 MiB; each acquisition allows 20 current or pending documents.
Downloads preserve validated source bytes, including any embedded metadata. Labels, format, byte
size and upload date distinguish files. A stored report is not verification of authenticity, and
format checks are not antivirus certification. Encrypted, malformed and active-content PDFs are
rejected. Rename a label or explicitly remove a mistaken document; correcting file content means
adding the corrected copy and removing the mistaken one. Removal ends application access while
the provider's seven-day retention and holds still apply.

Archived pieces retain read-only paperwork. Failed loads and unavailable files are distinct from
having no documents. Retry failed downloads; for recovery-unavailable files, contact the administrator
or add another copy through an active linked piece. An uncertain upload keeps its exact request in
memory: Check and retry resolves its recorded outcome before resubmission. Browser closure and
sign-out do not preserve drafts.

Open Export records from Collection or Archive to prepare CSV with acquisition facts, or a ZIP
with current stored detail photographs and acquisition documents. Shared documents appear once;
they may describe pieces outside the chosen scope, and their contents are not redacted. Select
active records or active plus archived records; search and loaded pages do not
narrow the scope. Follow the [format and spreadsheet guide](collection-export.md) for limits,
contents, safe decoding and failures. These files cannot restore Workbench.

## Usability and boundaries

Appearance follows the system until an explicit preference is selected. The sign-in switch toggles
Light/Dark; the signed-in account menu cycles Auto → Dark → Light → Auto, allowing a return to system
appearance. Desktop/mobile layouts and appearance changes preserve task state. Collection records,
photos and in-session drafts are private to the authorized tenant; signing out clears protected
client state. Appearance is the persisted local preference. Accounting, commerce, bulk quantities,
classification and valuation are separate workflows, not prerequisites for keeping a collection.

The [first hobbyist scenario](specs/2026-09-06-first-hobbyist-scenario.md#completion-and-validation)
retains the integrated phone-to-desktop journey and human-validation requirements. Automated tests,
a recording or a completed technical increment do not establish that a collector can complete the
journey without developer intervention.

## Design records

| Area | Historical reasoning and acceptance constraints |
| --- | --- |
| Save and browse | [H1](specs/2026-09-06-h1-collection-notebook.md) |
| Photographs | [H2](specs/2026-09-07-h2-item-photographs.md) |
| Search | [H3](specs/2026-09-07-h3-collection-search.md) |
| Edit | [H4](specs/2026-09-07-h4-item-editing.md) |
| Archive | [H5](specs/2026-09-07-h5-item-archiving.md) |
| Restore | [H6](specs/2026-09-07-h6-archive-recovery.md) |
| CSV and photographs package | [H7](specs/2026-09-08-h7-collection-export.md), [H8](specs/2026-09-08-h8-collection-package.md) |
| Acquisition context | [H9](specs/2026-09-09-acquisition-context.md) |
| Shared acquisition relationships | [H10](specs/2026-09-09-shared-acquisitions.md) |
| Acquisition documents | [H11](specs/2026-09-11-h11-acquisition-documents.md) |
| Acquisition-aware exports | [H12](specs/2026-09-11-h12-acquisition-export.md) |
