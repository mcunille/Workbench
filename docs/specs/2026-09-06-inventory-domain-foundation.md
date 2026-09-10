# Inventory domain foundation for H1

**Status: Accepted; partially implemented** — the owner approved this foundation and implementation
of H1 following inspection of GemInv. The individually tracked collection slice is implemented;
expanded stock-management, quantities and component workflows remain outside that delivery.
The [collection guide](../collection.md) owns the supported product scope.

## Purpose

Keep the first collection form small while fixing the meaning of its records before data exists.
Gemstones, jewelry, materials, and consumables must fit without category-specific quantity rules,
lost component identity, or mutable descriptive fields becoming financial history.

The goal is stable domain boundaries, not an attempt to prebuild every future workflow. Later
features may add tables, but must not reinterpret an existing item as a product definition,
purchase line, balance, or accounting entry.

## GemInv evidence

Inspected the local `F:/Sources/Git/GemInv` checkout at commit
`83358c232708ae809ca35afc15f54f6c9eb892aa`. This is source inspection, not runtime verification.

| Source relative to GemInv | Observed behavior | Workbench lesson |
| --- | --- | --- |
| `src/api/GemInv.Domain/Entities/InventoryItem.cs` | One root combines identity, category, status, optional quantity/unit, purchase source, and cost basis. | Give the root one stable meaning and separate descriptive, stock, and financial behavior. |
| `src/api/GemInv.Api/Endpoints/InventoryEndpoints.Validation.cs` and `InventoryEndpoints.cs` | Gemstone quantity is discarded; jewelry quantity is optional; Other requires positive quantity and a unit. Updates directly assign quantity. | Category must not determine whether an item is individual or quantity-tracked. A quantity edit is not a consumption history. |
| `src/api/GemInv.Domain/Entities/GemstoneDetails.cs` and `JewelryDetails.cs` | Typed per-category details; gemstone treatments and reports have distinct representations. | Retain typed relational detail tables and distinguish unknown facts from confirmed negatives. |
| `src/api/GemInv.Domain/Entities/JewelryGemstone.cs` | Embedded gemstone descriptions have count and total weight but no FK to a standalone inventory stone. | A description of accent stones and the identity of a mounted tracked stone are different records. |
| `src/api/GemInv.Domain/Entities/PurchaseOrderLineItem.cs` | A purchase line has quantity/unit and generates multiple inventory items. | Purchase documents and physical holdings are different entities. |
| `src/api/GemInv.Domain/Entities/InvoiceLineItem.cs` | Sale lines reference an inventory item without a sold-quantity field. | Whole-item sale semantics do not establish partial stock sale semantics. |
| `src/api/GemInv.Domain/Enums/InventoryItemType.cs`, `UnitOfMeasure.cs`, and `ExpenseCategory.cs` | Gemstone/Jewelry/Other; Grams/Pieces/Millimeters; SuppliesConsumables is an expense category. | Materials and consumables are not implemented as independent stock domains in this model. Expense classification does not measure remaining supply. |
| `docs/design/inventory.md` | Structured details superseded a JSON attributes bag; cost allocation couples inventory with purchase documents. | Keep typed data, but design future valuation and acquisition relationships explicitly rather than adding mutable money fields to the notebook. |

No stock-movement or work-order entity was found in the inspected domain source. GemInv supplies
useful requirements and experience, but does not prove those later workflows are solved.

## Core decisions

### One item is one physical holding

`Inventory.Items` represents a particular physical object or an identified lot of interchangeable
stock. It never means the general product "sterling silver wire" across all purchases and locations.
Reusable product definitions can be introduced separately if a workflow needs them.

Every item has an immutable `TrackingKind`: `Individual` or `Lot`. H1 creates only Individual
records. A database constraint restricts H1 to that supported value; the later lot feature expands
the constraint and adds its required stock tables together. Do not expose a nonfunctional lot option.

- Individual: one independently identifiable object. Count is implicitly one, not an editable
  quantity. A stone weighing 2 carats is one stone; its weight is a measurement, not a stock count.
- Lot: an identified holding consumed, split, or sold in quantities. It needs a unit and stock
  movements before it can be created. A parcel may need both piece count and measured total weight;
  do not assume an exact per-piece conversion.
- A sealed bottle can be an individual object if only its whereabouts matter. Tracking its contents
  later creates an explicit linked stock holding; it does not turn the bottle's identity into a
  mutable number of milliliters.
- Splitting a lot into individually identified stones creates new item identities with lineage and
  removes the corresponding stock from the lot atomically. It does not switch the original row's kind.

### Classification and use do not control tracking

Gemstone, jewelry, and material describe what a holding is. Consumable describes how it is used:
a material can be incorporated into jewelry, consumed during polishing, or sold. Do not introduce
a mutually exclusive four-value type that makes Material and Consumable incompatible.

H1 requires no classification. Future optional, typed profiles enrich the same identity:
`GemstoneDetails`, `JewelryDetails`, and material specifications. Profile validation belongs with
the corresponding domain behavior. The exact profile compatibility rules need their feature spec;
do not prebuild unrestricted multiple profiles or invent an EAV/JSON attribute engine.

The user-entered `Name` remains valid for every item. A calculated gemological description is
additional information and never removes the owner's name because the category changed.

### Measurements, balances, and money are separate

Measurements describe an object: weight, dimensions, purity, and uncertainty. Stock balances
describe availability in a declared unit. Valuation describes cost in a currency at a defined
business event. Do not use one generic Quantity or Value field for these different meanings.

The future stock subsystem must establish decimal precision and bounds, unit dimensions, whole-piece
rules, explicit conversion rules, and transactional receipt/issue/transfer/adjustment operations.
Movement history is authoritative; any cached balance is maintained in the same transaction.
Competing consumption must serialize on the affected holding/balance and reject insufficient stock
unless a separately approved negative-stock policy exists. Corrections preserve posted history.
These are constraints for the future design, not approval of a specific stock ledger schema today.

H1 stores a free-text `StorageLocation`, explicitly the descriptive whereabouts of an individual
object. It is not a stock balance key. Future lots may hold quantities at multiple structured
locations; do not make a single root location string authoritative for that workflow. Preserve H1
location text when introducing structured locations rather than silently treating text as identifiers.

### Composition preserves identity and history

A tracked stone mounted into a ring retains its item ID, photographs, and reports. Future assembly
operations relate the stone to the ring with effective history. They must prevent cycles, concurrent
active membership in two assemblies, and double-counting an assembled component as independently
available stock. Removal ends the relationship without deleting either item.

An entry such as "12 unidentified accent diamonds, 0.20 ct total" can remain descriptive composition
without manufacturing twelve false item identities. If stones become individually tracked, a
deliberate operation reconciles the description with linked items. Description alone changes no stock.

Work orders distinguish assembly (identity retained), transformation (inputs become new outputs),
and consumption (stock used up). They record input/output lineage and quantities; they do not
simulate these operations by changing categories or editing descriptions.

### Documents reference inventory without owning it

Do not add a required purchase-order parent or one universal purchase-source FK to `Items`.
Manual entry remains legitimate. Later receipt/allocation records connect purchases to holdings,
supporting partial receipts, splits, combinations, and acquisitions from multiple sources.

Reservations, possession, assembly membership, sale history, and archival are separate concepts.
Do not collapse them into a universal root Status enum. Future accounting records preserve their
issued values and currency independently of changing item descriptions or current valuations.

All child/link tables use tenant-qualified foreign keys and the existing SQL isolation boundary.
An item UUID is permanent and never reused; a later human-readable stock label is a separate field.

## Revised H1 physical schema

| Column | SQL Server type | Rule |
| --- | --- | --- |
| Id | uniqueidentifier | Server-assigned primary key, never reused. |
| TenantId | uniqueidentifier | Required tenant FK; ownership cannot change. |
| TrackingKind | varchar(16) | Required; H1 CHECK permits only Individual. Immutable through application commands. |
| Name | nvarchar(200) | Required user name; normalized nonblank. |
| Notes | nvarchar(4000), nullable | Plain text; unknown/absent is null. |
| StorageLocation | nvarchar(200), nullable | Descriptive whereabouts of the individual object. |
| CreatedAtUtc | datetimeoffset | Required server UTC timestamp. |
| CreationRequestId | uniqueidentifier | Required nonempty UUID for duplicate-safe creation. |
| RowVersion | rowversion | Database concurrency token; H1 is read/create only, future edits require a checked token. |

Retain the tenant-qualified identity and creation-request uniqueness, RLS, grants, validation,
replay behavior, and recovery rules from the H1 spec. Order browsing by `(CreatedAtUtc, Id)` with
a tenant-leading index and a cursor containing both ordering values. This provides chronological
traversal rather than arbitrary UUID order. `StorageLocation` maps to API `location` for the
notebook; clients cannot submit tenant, tracking kind, timestamps, or rowversion on creation.

No placeholder category, stock, composition, or financial tables ship in H1. The intentional
additions are explicit Individual semantics and a concurrency token, not the future features.

## Scenarios the foundation must accommodate

| Scenario | Required interpretation |
| --- | --- |
| Unknown blue stone later identified as sapphire | Same Individual ID and owner name; add gemological facts. |
| Sapphire mounted in a ring, then removed | Two persistent IDs and historical component membership; no duplicate available stone. |
| Ten grams issued from 100 grams of solder | Lot movement of 10 grams; 90 grams remain; no item-description mutation. |
| Two users each request 60 grams from 100 grams | At most one issue succeeds under a no-negative-stock policy. |
| Three stones identified from a parcel | Three Individual outputs linked to a reduced source lot; reconcile recorded count/weight explicitly. |
| Four identical chains, one sold | Lot issue of one piece, or four Individual records if independently identified. |
| Rough stone cut into two gems | New output identities linked to consumed input, with recorded yield/loss. |
| Personal ring entered without purchase paperwork | Valid Individual record with no invented acquisition or cost. |
| Supply bought as an expense | Accounting purpose does not imply zero stock or automatic physical consumption. |

These walkthroughs check the conceptual boundaries. Each later workflow still requires its own
accepted contracts and real concurrency tests. Adding those features should extend this model,
not change the meaning of the H1 rows already entrusted to it.
