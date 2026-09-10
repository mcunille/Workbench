# H10 verification record

Implementation base: `1cd7166f2a4049c0b16ef680d3d1a3249d4d815c`.
The owner approved the [shared acquisition design](../../specs/2026-09-09-shared-acquisitions.md)
on 2026-09-10.

## Focused verification

- TDD: the initial real-SQL link test returned 404 instead of the expected 200 before the
  implementation. The initial client tests failed for the missing acquisition picker,
  shared-edit scope notice and archived acquisition navigation.
- The expanded backend run passed 95 acquisition, provisioning and readiness tests, including
  five competing HTTP request scenarios against SQL Server. Deterministic lock-wait evidence
  and mutation results are recorded below after completion.

## Delivery evidence

Verification is in progress. Full gate, container smoke, preview inspection, narrated capture
and independent review outcomes will be recorded here before delivery.

## Scope limits

Automated scenarios do not establish uncoached collector usability. No production migration,
deployment or traffic change is part of this work. H11 documents, H12 exports, financial fields,
purchase orders, lots, assembly relationships and public sharing are excluded.

Generated media remains outside Git. See [reproduction instructions](README.md).
