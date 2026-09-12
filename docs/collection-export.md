# Collection export formats

Open Export records from Collection or Archive. Choose Records (CSV) or Records, photographs and
acquisition documents (ZIP), select Active records or Active and archived records, then Prepare
export. Every record in that scope is included, irrespective of search or loaded pages. Download
appears only when the complete file has arrived. A retry prepares a new snapshot; records may
have changed. A downloaded file is a local copy you control.

CSV includes current acquisition facts. Photographs and acquisition paperwork require ZIP.
Shared documents may describe pieces outside the chosen scope: written text and document contents
are not redacted. These are collector-recorded facts and files, not verified provenance.

## CSV version 2

Current filenames begin `workbench-records-v2-`. Files use UTF-8 with a byte-order mark, commas,
CRLF record separators, and one header. Every field is double-quoted; embedded double quotes are
doubled. Newlines and Unicode inside fields remain data. Use a CSV parser rather than splitting
lines or commas. No `sep=` line is added.

Version 2 preserves the first eleven version 1 columns and appends eight acquisition columns:

| Column | Meaning |
| --- | --- |
| schema_version | `2` for the current contract. |
| exported_at_utc | One timestamp for the serializable read, repeated on all rows. |
| scope | `active` or `all`, repeated on all rows. |
| item_id | Stable hyphenated UUID. |
| tracking_kind | Current holding kind, `Individual`. |
| name | Name with the safety encoding below. |
| notes | Notes with safety encoding; empty means absent. |
| location | Descriptive location with safety encoding; empty means absent. |
| is_archived | `true` or `false`; archiving is separate from ownership, sale, or disposal. |
| created_at_utc | Original creation timestamp. |
| archived_at_utc | Archive timestamp, or empty for an active record. |
| acquisition_id | Stable hyphenated UUID of the shared acquisition, or empty when unlinked. |
| acquisition_method | `Purchase`, `Gift`, `Inheritance`, `Trade`, `Other` or `Unknown`. |
| acquisition_source | Optional source with safety encoding. |
| acquisition_date_precision | `unknown`, `year`, `month` or `day`. |
| acquisition_year | Known year, otherwise empty. |
| acquisition_month | Known month, otherwise empty. |
| acquisition_day | Known day, otherwise empty. |
| acquisition_notes | Optional collector-recorded notes with safety encoding. |

All eight acquisition columns are empty for an unlinked piece. Shared facts repeat on each
included piece with the same acquisition ID. Partial dates retain their precision; unknown
components stay empty, without inventing January 1 or today's date. CSV contains no document
inventory or file bytes.

Timestamps use UTC ISO 8601 with seven fractional digits and `Z`, for example
`2026-09-08T07:00:00.0000000Z`. Rows are ordered by creation time and SQL Server item-ID order.
The result describes one committed collection state; its timestamp is not a time-travel query.
Changes after that read are reflected only by preparing a new export.

### Spreadsheet safety and recovering literal text

Import columns as Text to preserve exact IDs, timestamps, and user-entered text. Every present
name, notes, location, acquisition_source and acquisition_notes value has exactly one added ASCII
apostrophe before CSV quoting. For example `=1+2` becomes `'=1+2`, and `'original` becomes
`''original`. Empty optional fields remain empty. After CSV parsing, remove exactly one leading
apostrophe from each present user-text field to recover the original. Do not remove prefixes from
system columns, including acquisition method, precision, dates and IDs.

This keeps formula-leading characters away from the beginning of user-text cells. Quoting alone
is not formula protection. Spreadsheet software may display the apostrophe, and automatic import
behavior varies. Removing the prefix or re-saving can remove protection. No claim is made that
all spreadsheet applications interpret arbitrary imported text identically.

## Records, photographs and acquisition documents (ZIP)

Current filenames begin `workbench-package-v2-`. The package has `records.csv` (CSV version 2),
`manifest.json`, `README.txt`, `photos/<item_id>.webp`, and
`documents/<acquisition_id>/<document_id>.<extension>`. Use a ZIP extractor, text editor, CSV reader
and ordinary document viewers; photographs need a WebP-capable viewer. Camera originals,
thumbnails and retired photographs are excluded. Documents preserve exact stored validated bytes,
including embedded metadata. Paths use canonical server UUIDs and validated extensions (`pdf`,
`jpg`, `png`, `webp`); labels and original filenames never become paths.

Match permanent item IDs across CSV, manifest and photograph filenames. Match acquisition IDs to
shared facts, included pieces and document directories. Each referenced acquisition appears once,
with every current committed document included once even when several selected pieces share it.
The package excludes orphan acquisitions, pending uploads, removed documents and excluded piece
identities, links, names and photographs. A shared document may still describe an excluded piece.
An empty documents array means there were no current documents in that snapshot.

### Package manifest version 2

`manifest.json` is UTF-8 JSON. User text is literal and needs no CSV apostrophe decoding.

| Field | Meaning |
| --- | --- |
| `package_version` | Integer `2`. |
| `scope` | `active` or `all`, matching CSV. |
| `exported_at_utc` | The same UTC snapshot timestamp as CSV. |
| `record_count` | Number of included items. |
| `photo_count` | Number with an included detail photograph. |
| `acquisition_count` | Number of distinct acquisitions referenced by included items. |
| `document_count` | Number of current documents included once across those acquisitions. |
| `items` | One entry per record with `item_id`, literal `name`, nullable `location`, nullable `acquisition_id`, and `photo`. |
| `photo.status` | `none` if absent in the snapshot, otherwise `included`. |
| Included photo fields | `path`, `media_type` (`image/webp`), `byte_length`, and `sha256` (64 hexadecimal characters). These fields are absent for `none`. |
| `acquisitions` | One entry per referenced acquisition with `acquisition_id`, `method`, nullable `source`, `date_precision`, nullable `year`/`month`/`day`, nullable `notes`, `included_item_ids` and `documents`. |
| `included_item_ids` | Only selected pieces sharing this acquisition, in export order. This is a scoped relationship list. |
| `documents` | Current files, each with `document_id`, literal `label`, `created_at_utc`, `path`, `media_type`, `byte_length` and `sha256`. |

Checksums are SHA-256 of the stored bytes. README explains extraction, IDs, scoped relationships,
partial dates, CSV decoding and backup exclusions. Missing, corrupt, unavailable or unreadable
required photographs or documents fail the entire package; they never become an absent photo,
an empty document array, or a successful partial package.

## Limits, consistency and recovery

CSV supports 10,000 records and 32 MiB, with a 30-second preparation deadline. ZIP supports 10,000
records, 10,000 current documents, 32 MiB CSV, 16 MiB manifest, 128 MiB total uncompressed content
and 128 MiB final ZIP, with a two-minute deadline. Each document retains the 10 MiB upload bound.
If a bound is exceeded, no file is offered. Try Active records if archived records were included,
or choose CSV for records and acquisition facts; CSV excludes photographs and paperwork. Search
cannot reduce scope. Empty scope, busy preparation, failed preparation and lost authorization
have separate outcomes. Preparation can briefly delay edits.

Items, acquisition relationships and facts, and committed photo/document revision references are
captured in one tenant-filtered serializable transaction. Blob reads then use captured immutable
revisions with digest and length verification. Concurrent edits, links, archive/restore, renames
or removals produce one coherent successful snapshot or a truthful failure. Seven-day retention
protects captured revisions during the two-minute preparation. No database locks span provider
reads. CSV captures the same acquisition facts without reading document blobs.

Retry prepares a new snapshot. Repeated photograph or document failures need operator investigation
using the [storage recovery runbook](operations/blob-and-service-providers.md); do not delete
entrusted files as a workaround. Cancelling or interrupted delivery never offers a partial file.
Changing format or scope discards the prepared file. Files expire after ten minutes and clear on
sign-out, identity change or reload. Private navigation and appearance changes preserve preparation.

<a id="format-version-1"></a>

## Previously downloaded version 1 files

Version 1 downloads remain valid; new exports produce version 2 with no version-selection option.
A v1 CSV has `schema_version` equal to `1` and only the first eleven columns listed above, ending
at `archived_at_utc`. Its byte encoding, quoting, ordering and timestamp rules are unchanged.
Decode exactly one added apostrophe from each present `name`, `notes` and `location` field.
Acquisition facts are absent from v1.

<a id="records-and-photographs-zip"></a>

A v1 ZIP has `package_version` equal to `1`, `records.csv` in CSV version 1, `README.txt`,
`manifest.json` and `photos/<item_id>.webp`. Its manifest has `scope`, `exported_at_utc`,
`record_count`, `photo_count` and `items`; each item has `item_id`, literal `name`, nullable
`location` and `photo` with the same `none`/`included` semantics and included-photo metadata
described above. It has no acquisition IDs, counts, acquisition array or document entries.
Manifest text is literal. Match the item ID to its photograph path and verify its byte length
and SHA-256 as in v2.

## This is not a backup

Exports contain current selected collection records and acquisition facts, with stored photographs
and documents only in ZIP. They exclude edit/lifecycle history, creation replay payloads, identity
and session data, and the rest of the application database. They cannot restore Workbench, and no
import workflow is supplied. Use operational backup and recovery runbooks for application recovery.
This guide owns the format contract; dated H7, H8 and H12 specs retain the design reasoning.
