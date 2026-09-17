// Copyright (c) 2026 The White Stag Collection.
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace Workbench.Server.Purchasing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftEntry(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired, MaxLength(500)] string? Description,
    [property: JsonRequired, MaxLength(2000)] string? Notes,
    [property: JsonRequired, MaxLength(2048)] string? SourceLink,
    [property: JsonRequired] string? IndicativePrice,
    [property: JsonRequired] string? Quantity,
    [property: JsonRequired] string? UnitOfMeasure,
    [property: JsonRequired] string PriceMode,
    [property: JsonRequired] string? Price,
    [property: JsonRequired] DraftLegacyPricing? LegacyPricing,
    [property: JsonRequired, MaxLength(200)] string? SupplierSku,
    [property: JsonRequired, MaxLength(100)] string? ItemType);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftContent(
    [property: JsonRequired, MaxLength(200)] string? Title,
    [property: JsonRequired, MaxLength(200)] string? SupplierName,
    [property: JsonRequired, MaxLength(3)] string? Currency,
    [property: JsonRequired, MaxLength(10000)] string? Notes,
    [property: JsonRequired, MaxLength(20)] IReadOnlyList<string> SourceLinks,
    [property: JsonRequired, MaxLength(100)] IReadOnlyList<DraftEntry> Entries,
    [property: JsonRequired] Guid? SupplierId,
    [property: JsonRequired, MaxLength(200)] string? SupplierContactName,
    [property: JsonRequired, MaxLength(254)] string? SupplierEmail,
    [property: JsonRequired, MaxLength(100)] string? SupplierPhone,
    [property: JsonRequired, MaxLength(2048)] string? SupplierWebsite,
    [property: JsonRequired, MaxLength(2000)] string? SupplierPostalAddress,
    [property: JsonRequired, MaxLength(200)] string? SupplierOrderReference,
    [property: JsonRequired, MaxLength(200)] string? Platform);


[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDraftOrderRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] DraftContent Draft);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateDraftOrderRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] string ExpectedVersion, [property: JsonRequired] DraftContent Draft);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateDraftOrderRequest([property: JsonRequired] DraftContent Draft);
public sealed record DraftOrderResponse(Guid Id, DraftContent Draft, string CreatedAtUtc, string UpdatedAtUtc,
    string Version, string PoReference, bool SupplierIsArchived, DraftCalculationResponse Calculation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftLegacyPricing(
    [property: JsonRequired] string? Quantity,
    [property: JsonRequired] string? UnitOfMeasure,
    [property: JsonRequired] string? UnitPrice,
    [property: JsonRequired] string? PricingUnit,
    [property: JsonRequired] string? PricePerQuantity,
    [property: JsonRequired] string? PricingQuantity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeleteDraftOrderRequest([property: JsonRequired] Guid RequestId,
    [property: JsonRequired] string ExpectedVersion);

public sealed record SaveDraftOrderResponse(Guid RequestId, bool Replayed, Guid DraftOrderId, string SavedVersion, string CompletedAtUtc);
public sealed record DraftLineCalculation(Guid Id, string? Gross);
public sealed record DraftCalculationResponse(IReadOnlyList<DraftLineCalculation> Lines, int IncompleteLineCount, string? MerchandiseEstimate);
