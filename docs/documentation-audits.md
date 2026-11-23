# Documentation audits

Use the installed `doc-audit` skill for requested documentation audits and cleanup. The
skill owns evidence-based review, distillation, retirement, and cleanup verification.
This guide owns Workbench's scope defaults and persistent review bookkeeping.

## Scope and bookkeeping

Start with the requested scope and [documentation index](README.md). For an oldest/next
batch, default to clean tracked Markdown specs under `docs/specs/`, excluding `README.md`,
and select ten documents. An explicit user scope or count takes precedence.

Use `docs/doc-audit-ledger.json` across runs and checkouts. Workbench adopts the portable
version-1 JSON contract and queue/completion rules in the skill's repeatable-batch reference.
Read that reference for oldest/next selection or ledger tracking, including deterministic
Git-based ordering, exact reviewed revisions, dirty candidates, concurrent updates, and
verified retirement or rename handling. A missing ledger means no recorded reviews.

Completing a requested audit authorizes only completed-review ledger bookkeeping in addition
to findings. Explicit findings-only, read-only, or no-writes requests suppress this bookkeeping.
Recording an outcome does not approve cleanup, spec deletion, or collaboration writes.
Report ledger changes separately from proposed documentation edits. Follow Workbench's
[workflow and delivery rules](development-workflow.md) for authorized bookkeeping and cleanup.

Keep inventories and detailed evidence in the workflow's ignored workspace when writes are
allowed; the compact ledger is the durable audit record. A no-writes run reports its findings
and any proposed ledger entries in the response.

## Ownership and installation

`doc-audit` is maintained in the private `mcunille/skills` repository and installed at user
scope in `~/.agents/skills/`, alongside the skills covered by
[installed skill ownership](development-workflow.md#skill-installation-and-ownership).
Workbench keeps this policy and its ledger, rather than a vendored skill. Install the complete
skill directory, including its references, license, provenance, and agent metadata. Inspect
existing installations before replacement and avoid same-name project copies.

The reviewed migration source is skills revision `01399fabb9bc3d5c437851745b20eb4dce25b076`:

```powershell
npx --yes skills@1.7.2 add 'mcunille/skills#01399fabb9bc3d5c437851745b20eb4dce25b076' --agent codex --skill doc-audit --global --copy
```

Authorized users can also copy the complete `skills/doc-audit/` directory from a local clone
at that revision. Review later revisions before updates. Refresh or restart Codex as needed
and verify discovery; file installation alone does not prove live invocation. Missing required
skills must be reported before the audit. Workbench CI and contributors need no private-library
access for application verification. Installation grants no additional write authority.
