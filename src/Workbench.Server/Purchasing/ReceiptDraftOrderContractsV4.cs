// Copyright (c) 2026 The White Stag Collection.
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace Workbench.Server.Purchasing;

// Frozen historical fingerprint-4 declaration order and required fields.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReceiptDraftEntryV4(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired, MaxLength(500)] string? Description,
    [property: JsonRequired, MaxLength(2000)] string? Notes,
    [property: JsonRequired, MaxLength(2048)] string? SourceLink,
    [property: JsonRequired] string? IndicativePrice,
    [property: JsonRequired] string? Quantity,
    [property: JsonRequired] string? UnitOfMeasure,
    [property: JsonRequired] string PriceMode,
    [property: JsonRequired] string? Price,
    [property: JsonRequired] ReceiptDraftLegacyPricingV4? LegacyPricing,
    [property: JsonRequired, MaxLength(200)] string? SupplierSku,
    [property: JsonRequired, MaxLength(100)] string? ItemType);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReceiptDraftContentV4(
    [property: JsonRequired, MaxLength(200)] string? Title,
    [property: JsonRequired, MaxLength(200)] string? SupplierName,
    [property: JsonRequired, MaxLength(3)] string? Currency,
    [property: JsonRequired, MaxLength(10000)] string? Notes,
    [property: JsonRequired, MaxLength(20)] IReadOnlyList<string> SourceLinks,
    [property: JsonRequired, MaxLength(100)] IReadOnlyList<ReceiptDraftEntryV4> Entries,
    [property: JsonRequired] Guid? SupplierId,
    [property: JsonRequired, MaxLength(200)] string? SupplierContactName,
    [property: JsonRequired, MaxLength(254)] string? SupplierEmail,
    [property: JsonRequired, MaxLength(100)] string? SupplierPhone,
    [property: JsonRequired, MaxLength(2048)] string? SupplierWebsite,
    [property: JsonRequired, MaxLength(2000)] string? SupplierPostalAddress,
    [property: JsonRequired, MaxLength(200)] string? SupplierOrderReference,
    [property: JsonRequired, MaxLength(200)] string? Platform);


[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReceiptDraftLegacyPricingV4(
    [property: JsonRequired] string? Quantity,
    [property: JsonRequired] string? UnitOfMeasure,
    [property: JsonRequired] string? UnitPrice,
    [property: JsonRequired] string? PricingUnit,
    [property: JsonRequired] string? PricePerQuantity,
    [property: JsonRequired] string? PricingQuantity);
