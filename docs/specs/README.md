# Workbench specifications

Specs capture the requirements and reasoning for one meaningful change. They are durable design
history, not a backlog and not a substitute for current documentation.

## When to write a spec

Write a spec when a change introduces meaningful product behavior or changes a durable boundary,
including:

- a workflow or accounting rule;
- a public contract or data model;
- a security or privacy property;
- a dependency, storage, deployment, or operational commitment;
- behavior shared by more than one product area.

Small corrections, routine maintenance, and changes already governed by an accepted spec generally
do not need a new one.

## Naming

Use a date and a short descriptive name:

```text
YYYY-MM-DD-short-topic-name.md
```

## Status

Every spec begins with a status:

- **Proposed** — under discussion and not approved for implementation.
- **Accepted** — approved as the direction to implement.
- **Implemented** — reflected in the product and its living documentation.
- **Superseded** — replaced by a linked, newer spec.

Acceptance of a spec approves its direction. It does not imply that the behavior has been built or
deployed.

## Suggested structure

Scale the document to the change, but normally cover:

1. summary and status;
2. problem and affected users;
3. goals, non-goals, constraints, and invariants;
4. current behavior and evidence;
5. proposed design and affected boundaries;
6. alternatives and why they were rejected;
7. security, privacy, failure, and recovery behavior;
8. compatibility, migration, and rollback;
9. verification and success criteria;
10. residual risks and unresolved questions.

When implementation makes a spec true, update the appropriate living documentation and change the
spec status to **Implemented**. Preserve the spec so future contributors can understand why the
current design exists.
