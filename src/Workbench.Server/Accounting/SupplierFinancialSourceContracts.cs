// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public sealed record SupplierCreditClaimEvidence(Guid ClaimId, Guid BillId, Guid BillRevisionId,
    Guid RecognitionEventId, string ComponentKey, string Amount, string Treatment, Guid AccountId, Guid AccountVersion);

public sealed record SupplierCashSettlementEvidence(Guid Id, Guid CashSourceId, Guid ItemId, string Amount,
    DateOnly PostingDate, DateTimeOffset RecordedAtUtc, Guid? ReleaseGroupId);

public sealed record SupplierSourceCorrectionEvidence(Guid Id, string SourceKind, Guid OriginalSourceId,
    Guid? ReplacementSourceId, Guid GroupId, string Reason, DateOnly PostingDate, DateTimeOffset RecordedAtUtc);

public sealed record SupplierFinancialSourceEvidence(string SourceKind, Guid SourceId, Guid SourceRevisionId,
    string Amount, DateOnly DocumentDate, DateOnly EffectiveDate, DateOnly PostingDate, DateOnly? ActualDate,
    string? Method, string? Reference, Guid? FundingAccountId, Guid? FundingAccountVersion,
    IReadOnlyList<SupplierCreditClaimEvidence> Claims, IReadOnlyList<SupplierCashSettlementEvidence> Settlements,
    IReadOnlyList<SupplierSourceCorrectionEvidence> Corrections);
