// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Serialization;
namespace Workbench.Server.Purchasing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CommitPurchaseOrderRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] string ExpectedVersion, [property: JsonRequired] string OrderDate);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AmendPurchaseOrderRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] string ExpectedVersion, [property: JsonRequired] string OrderDate, [property: JsonRequired] string Reason, [property: JsonRequired] DraftContent Draft);
public sealed record SavePurchaseOrderResponse(Guid RequestId, bool Replayed, Guid DraftOrderId, string SavedVersion, string CompletedAtUtc, int Revision);
public sealed record PurchaseOrderResponse(Guid Id, DraftContent Draft, string CreatedAtUtc, string UpdatedAtUtc, string Version, string PoReference, bool SupplierIsArchived, DraftCalculationResponse Calculation, string State, string? OrderDate, int Revision);
public sealed record PurchaseOrderSummary(Guid Id, string? Title, string? SupplierName, string UpdatedAtUtc, string PoReference, string? SupplierOrderReference, string? Platform, string State, string? OrderDate, int Revision);
public sealed record PurchaseOrderPageResponse(IReadOnlyList<PurchaseOrderSummary> Items, string? NextCursor);
public sealed record PurchaseOrderRevisionSummary(int Revision, string OrderDate, Guid ActorUserId, string RecordedAtUtc, string? Reason, int CalculationPolicyVersion);
public sealed record PurchaseOrderRevisionResponse(int Revision, string OrderDate, Guid ActorUserId, string RecordedAtUtc, string? Reason, int CalculationPolicyVersion, DraftContent Draft, DraftCalculationResponse Calculation, string PoReference);
public sealed record PurchaseOrderRevisionPageResponse(IReadOnlyList<PurchaseOrderRevisionSummary> Items, string? NextCursor);
