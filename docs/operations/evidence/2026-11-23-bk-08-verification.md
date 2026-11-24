# BK-08 verification on November 23, 2026

BK-08 installs internal supplier credit, refund, repayment and source correction commands.
The release adds authorized readback while keeping public financial entry and bookkeeping activation
gated. The installed migration is `20261019010000_AddSupplierCreditsAndRefunds`.

The move-only extraction in `d02475b` passed 78 characterization tests on Linux with disposable SQL
Server. Coverage was measured on each resulting file under the approved coverage exception.

| File | Lines | Branches |
| --- | ---: | ---: |
| JournalEntryQueries.cs | 94.1% | 98.3% |
| JournalReportQueries.cs | 95.7% | 81.0% |
| SupplierReconciliationQueries.cs | 96.6% | 96.2% |
| SupplierReconciliationSnapshot.cs | 100.0% | 100.0% |
| SupplierReconciliationValidation.cs | 100.0% | 86.0% |

The readback implementation passed 36 focused SQL and HTTP regression cases. Its eight feature
cases also passed after restoring deliberately altered source copies. Dropping the authentic cash
owner proof admitted bank-line damage with equal supplier totals. Discarding captured continuation
ceilings exposed a later correction on an older cursor. Both faults failed the intended assertions.
The current integrated solution built without warnings or errors. The generated client contract
passed type checking.

An upgrade test used the actual BK-07 predecessor on GitHub main `f46a81d` and posted a bill and a
payment with held evidence before installing BK-08. Historical rows and exact authorized retry
responses matched after installation. Copied release SQL without later-date availability protection
or credit document capture failed the expected overspend and hold assertions in disposable databases.
The selected source mutations were never applied to this worktree or its preview.

The paired recovery cases restore genuine credit, incoming refund, repayment and correction history,
then verify real available, missing and corrupt blob cases. Recovered accounting reports preserve the
four-control model and report the actual content state. These checks establish behavior for the tested
restore, permission and concurrency cases.

The container smoke passed its SQL migration, principal provisioning, internal TLS, cookie/session
and worker checks. A new isolated worktree preview served the current build at
`http://localhost:32769`. Authentication without an accounting role received 403. Assigning the
existing Accounting reader role enabled private, non-cacheable journal, item and reconciliation GETs.
The empty report reconciled to zero, a malformed cursor returned 400, and the purchase order list
returned 200. Posted examples were checked in the disposable SQL and HTTP suites.
