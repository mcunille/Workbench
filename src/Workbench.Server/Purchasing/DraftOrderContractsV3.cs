// Copyright (c) 2026 The White Stag Collection.
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace Workbench.Server.Purchasing;

// Persisted structured entries and historical fingerprint-3 shape, never exposed by OpenAPI.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DraftEntryV3(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired, MaxLength(500)] string? Description,
    [property: JsonRequired, MaxLength(2000)] string? Notes,
    [property: JsonRequired, MaxLength(2048)] string? SourceLink,
    [property: JsonRequired, MaxLength(20), RegularExpression(@"^(0|[1-9][0-9]{0,14})(\.[0-9]{1,4})?$")] string? IndicativePrice,
    [property: JsonRequired, MaxLength(14), RegularExpression(@"^(0|[1-9][0-9]{0,8})(\.[0-9]{1,4})?$")] string? Quantity,
    [property: JsonRequired, RegularExpression(@"^(piece|carat|gram|kilogram|ounce|troyOunce|millimeter|centimeter|meter|parcel|pair|set|pack|box|lot)$")] string? UnitOfMeasure,
    [property: JsonRequired, MaxLength(20), RegularExpression(@"^(0|[1-9][0-9]{0,14})(\.[0-9]{1,4})?$")] string? UnitPrice,
    [property: JsonRequired, RegularExpression(@"^(piece|carat|gram|kilogram|ounce|troyOunce|millimeter|centimeter|meter|parcel|pair|set|pack|box|lot)$")] string? PricingUnit,
    [property: JsonRequired, MaxLength(14), RegularExpression(@"^(0|[1-9][0-9]{0,8})(\.[0-9]{1,4})?$")] string? PricePerQuantity,
    [property: JsonRequired, MaxLength(14), RegularExpression(@"^(0|[1-9][0-9]{0,8})(\.[0-9]{1,4})?$")] string? PricingQuantity,
    [property: JsonRequired, MaxLength(200)] string? SupplierSku,
    [property: JsonRequired, MaxLength(100)] string? ItemType);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DraftContentV3(
    [property: JsonRequired, MaxLength(200)] string? Title,
    [property: JsonRequired, MaxLength(200)] string? SupplierName,
    [property: JsonRequired, MaxLength(3)] string? Currency,
    [property: JsonRequired, MaxLength(10000)] string? Notes,
    [property: JsonRequired, MaxLength(20)] IReadOnlyList<string> SourceLinks,
    [property: JsonRequired, MaxLength(100)] IReadOnlyList<DraftEntryV3> Entries,
    [property: JsonRequired] Guid? SupplierId,
    [property: JsonRequired, MaxLength(200)] string? SupplierContactName,
    [property: JsonRequired, MaxLength(254)] string? SupplierEmail,
    [property: JsonRequired, MaxLength(100)] string? SupplierPhone,
    [property: JsonRequired, MaxLength(2048)] string? SupplierWebsite,
    [property: JsonRequired, MaxLength(2000)] string? SupplierPostalAddress,
    [property: JsonRequired, MaxLength(200)] string? SupplierOrderReference,
    [property: JsonRequired, MaxLength(200)] string? Platform);
