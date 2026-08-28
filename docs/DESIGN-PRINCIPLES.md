# Workbench design principles

These principles guide product, domain, architecture, and implementation decisions. A proposed
change should explain how it follows them or why an explicit exception is justified.

## 1. Start simple, but correct

Build the smallest coherent version that preserves the domain's important truths. Simplicity is not
permission to corrupt financial history, weaken tenant isolation, or create data that cannot evolve.
Prefer a sound default over a generalized mechanism whose need has not been demonstrated.

## 2. Model the business before selecting the technology

Names, relationships, invariants, and workflows should reflect gemstone and jewelry work rather than
the preferences of a database or framework. Technology choices follow capability requirements and
must remain replaceable where replacement has credible value.

## 3. Provide progressive capability, not separate products

A hobbyist should be able to begin with inventory and collection management without confronting the
full professional system. A business should be able to add accounting, commerce, users, and controls
without migrating to a different product or incompatible data model.

## 4. Use ledger semantics where financial truth requires them

Not every record is a ledger entry. Inventory descriptions, purchase-order documents, listings, and
attachments have different lifecycles from posted financial effects. Ledger behavior belongs where
history, balance, traceability, and correction semantics are essential.

While an accounting period is open, authorized corrections may be made with enough history to
explain material changes. Closing a period freezes its posted financial effects. Later corrections
must be represented by explicit reversing or adjusting activity in an open period rather than by
rewriting closed history. Closing is an accounting boundary, not a blanket lock on every related
operational record.

## 5. Make configuration earn its complexity

Workbench must support the variation required to operate safely: tenant or business boundaries,
user roles, business profiles, and accepted gemstone taxonomies are known examples. Beyond those
needs, begin with deliberate defaults. Add a configuration point only after a concrete variation
cannot be handled correctly and simply without it.

## 6. Keep one open-source product

Local, self-hosted, and hosted Workbench use the same open-source product. The hosted service may
differentiate on convenience, operation, support, capacity, and service level—not by keeping core
product capabilities in a proprietary codebase.

## 7. Preserve ownership and portability of data

Users must be able to understand and retrieve their business data in useful forms. Avoid designs
that make a hosted provider the only practical path to access, migrate, back up, or restore it.
Storage choices must include a credible local-development and self-hosting story.

## 8. Separate information by its behavior, not by fashion

Financial postings, operational documents, searchable item data, and binary attachments may need
different integrity and lifecycle rules. Workbench may use more than one storage surface when those
rules justify it, but the choice of relational, document, or blob technology remains a design
decision—not a product principle.

## 9. Treat tenant isolation and authorization as invariants

Hosted multi-tenancy and self-hosted use must share clear ownership boundaries. Every operation must
act within an identified business context and an explicit user authority. Convenience must not rely
on hidden cross-tenant access or ambiguous ownership.

## 10. Make important state changes explainable

Users should be able to determine what changed, why it changed, and which business event caused the
change. Auditability should be proportional: immutable accounting history requires stronger controls
than an ordinary descriptive edit, but consequential changes should never become inexplicable.

## 11. Integrate without surrendering the core model

Marketplaces, payment services, tax tools, and other external systems are integrations, not the
owners of Workbench's inventory or accounting truth. Channel-specific concepts should be translated
at clear boundaries so that one provider does not define the whole product.

## 12. Defer irreversible choices

When requirements are still emerging, prefer decisions that are easy to revisit and data that can
be migrated. A spec proposing an expensive or difficult-to-reverse commitment must identify the
evidence for it, the exit strategy, and the cost of being wrong.
