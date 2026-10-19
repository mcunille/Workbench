// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Purchasing;

public sealed class SupplierSourceCorrection
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public string SourceKind { get; set; } = "";
    public Guid SourceId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public Guid? OriginalCreditId { get; set; }
    public Guid? OriginalBillId { get; set; }
    public Guid? OriginalCashSourceId { get; set; }
    public Guid? ReplacementSourceId { get; set; }
    public Guid? ReplacementSourceRevisionId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public string Currency { get; set; } = "";
    public byte[] OriginalSourceVersion { get; set; } = [];
    public DateOnly PostingDate { get; set; }
    public string Reason { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierSourceCorrectionMember
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CorrectionId { get; set; }
    public Guid GroupId { get; set; }
    public string Kind { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid? JournalId { get; set; }
    public Guid? RecognitionEventId { get; set; }
    public Guid? ItemId { get; set; }
    public string EffectsJson { get; set; } = "{}";
}

public sealed class SupplierCashSettlementRelease
{
    public Guid TenantId { get; set; }
    public Guid SettlementId { get; set; }
    public Guid CorrectionId { get; set; }
    public Guid GroupId { get; set; }
    public Guid JournalId { get; set; }
    public Guid RestoredMovementId { get; set; }
    public Guid OpeningMovementId { get; set; }
    public decimal Amount { get; set; }
    public Guid ClearingAccountId { get; set; }
    public Guid ClearingAccountVersion { get; set; }
    public DateOnly PostingDate { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}
