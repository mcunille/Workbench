// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;

namespace Workbench.Server.Purchasing;

internal sealed record SupplierBillSummary(Guid BillId, string State, Guid RevisionId, string Version, string? Reference);
internal sealed record SupplierBillPage(IReadOnlyList<SupplierBillSummary> Items, Guid? NextId);
internal sealed record SupplierBillEvidence(Guid DocumentId, Guid RevisionId, string Digest, long Length, string Label, bool Available);
internal sealed record SupplierBillDetail(Guid BillId, Guid PurchaseOrderId, Guid SupplierId, string Currency, string State,
    Guid RevisionId, string Version, string SupplierName, JsonElement Revision, JsonElement? Review, JsonElement? Posting, IReadOnlyList<SupplierBillEvidence> Evidence,
    Accounting.FinancialEvidenceSetResponse? FinancialEvidence = null);
internal sealed record SupplierBillHistoryItem(long Sequence, string Operation, Guid ActorId, DateTimeOffset RecordedAtUtc, JsonElement Result, JsonElement Command,
    JsonElement? Revision, JsonElement? Review);
internal sealed record SupplierBillHistoryPage(IReadOnlyList<SupplierBillHistoryItem> Items, long? NextSequence);
