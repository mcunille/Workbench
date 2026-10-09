// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public sealed record SupplierReportTotals(string Payable, string Advance, string CreditReceivable,
    string RefundClearing, string NetSupplierPosition);
public sealed record SupplierOpenItemSummary(Guid Id, string Kind, Guid SupplierId, Guid PurchaseOrderId,
    Guid? BillId, string Currency, string SourceKind, Guid SourceId, Guid SourceRevisionId,
    DateOnly SourcePostingDate, DateOnly? DueDate, string Balance, bool HasValidSource, DateOnly PostingThrough, DateTimeOffset RecordedThrough,
    FinancialEvidenceSetResponse? FinancialEvidence = null);
public sealed record SupplierItemHistoryEntry(Guid Id, Guid ItemId, Guid GroupId, long GroupSequence,
    string EventKind, Guid SourceEventId, Guid? JournalId, IReadOnlyList<Guid> ApplicationIds, Guid? CorrectionId, Guid? SupplierCorrectionId,
    DateOnly PostingDate, DateTimeOffset RecordedAtUtc, string Amount);
public sealed record SupplierControlBalance(Guid? AccountId, string Currency, string ControlFamily,
    string JournalAmount, string SubledgerAmount, string Difference, int MissingAttributionCount,
    int DuplicateAttributionCount, int InvalidSourceEvidenceCount, bool IsComplete);
public sealed record SupplierReconciliationSummary(SupplierReportPage<SupplierControlBalance> Controls,
    bool IsComplete, int UnresolvedTenantControlCount);
public sealed record SupplierReportPage<T>(IReadOnlyList<T> Items, string? NextCursor,
    DateOnly PostingThrough, DateTimeOffset RecordedThrough, SupplierReportTotals WholeFilterTotals,
    SupplierReportTotals PageTotals);
