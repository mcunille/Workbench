# Collection export CSV contract

Open Export records from Collection or Archive. Choose Active records or Active and archived
records, then Prepare export. Every record in that scope is included, irrespective of search or
loaded pages. Download CSV appears only when the complete file has arrived. A retry prepares a
new snapshot; records may have changed. A downloaded file is a local copy you control.

Export supports up to 10,000 records and 32 MiB. If either bound is exceeded, no file is offered;
try Active records if archived records were included. Empty scope, busy preparation, failed
preparation, and lost authorization have separate outcomes. Preparation can briefly delay edits.
The ready file expires after ten minutes and clears on sign-out, identity change, or reload.

## Format version 1

Files use UTF-8 with a byte-order mark, commas, CRLF record separators, and one header. Every
field is double-quoted; embedded double quotes are doubled. Newlines and Unicode inside fields
remain data. Use a CSV parser rather than splitting lines or commas. No `sep=` line is added.

| Column | Meaning |
| --- | --- |
| schema_version | `1` for this contract. |
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

Timestamps use UTC ISO 8601 with seven fractional digits and `Z`, for example
`2026-09-08T07:00:00.0000000Z`. Rows are ordered by creation time and SQL Server item-ID order.
The result describes one committed collection state; its timestamp is not a time-travel query.
Changes after that read are reflected only by preparing a new export.

## Spreadsheet safety and recovering literal text

Import columns as Text to preserve exact IDs, timestamps, and user-entered text. Every present
name, notes, and location value has exactly one added ASCII apostrophe before CSV quoting.
For example `=1+2` becomes `'=1+2`, and `'original` becomes `''original`. Empty optional fields
remain empty. After CSV parsing, remove exactly one leading apostrophe from each present user-text
field to recover the original. Do not remove prefixes from system columns.

This keeps formula-leading characters away from the beginning of user-text cells. Quoting alone
is not formula protection. Spreadsheet software may display the apostrophe, and automatic import
behavior varies. Removing the prefix or re-saving can remove protection. No claim is made that
all spreadsheet applications interpret arbitrary imported text identically.

## This is not a backup

CSV contains current item text and archive state. It excludes photos, edit/lifecycle history,
creation replay payloads, identity/session data, and the rest of the application database. It cannot
restore Workbench, and no import workflow is supplied. Use the operational backup and recovery
runbooks for application recovery.
