// Copyright (c) 2026 The White Stag Collection.
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace Workbench.Server.Purchasing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftEntryV4(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired, MaxLength(500)] string? Description,
    [property: JsonRequired, MaxLength(2000)] string? Notes,
    [property: JsonRequired, MaxLength(2048)] string? SourceLink,
    [property: JsonRequired] string? IndicativePrice,
    [property: JsonRequired] string? Quantity,
    [property: JsonRequired] string? UnitOfMeasure,
    [property: JsonRequired] string PriceMode,
    [property: JsonRequired] string? Price,
    [property: JsonRequired] DraftLegacyPricingV4? LegacyPricing,
    [property: JsonRequired, MaxLength(200)] string? SupplierSku,
    [property: JsonRequired, MaxLength(100)] string? ItemType,
    [property: JsonRequired] DraftDiscount? Discount = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[method: JsonConstructor]
public sealed record DraftContentV4(
    [property: JsonRequired, MaxLength(200)] string? Title,
    [property: JsonRequired, MaxLength(200)] string? SupplierName,
    [property: JsonRequired, MaxLength(3)] string? Currency,
    [property: JsonRequired, MaxLength(10000)] string? Notes,
    [property: JsonRequired, MaxLength(20)] IReadOnlyList<string> SourceLinks,
    [property: JsonRequired, MaxLength(100)] IReadOnlyList<DraftEntryV4> Entries,
    [property: JsonRequired] Guid? SupplierId,
    [property: JsonRequired, MaxLength(200)] string? SupplierContactName,
    [property: JsonRequired, MaxLength(254)] string? SupplierEmail,
    [property: JsonRequired, MaxLength(100)] string? SupplierPhone,
    [property: JsonRequired, MaxLength(2048)] string? SupplierWebsite,
    [property: JsonRequired, MaxLength(2000)] string? SupplierPostalAddress,
    [property: JsonRequired, MaxLength(200)] string? SupplierOrderReference,
    [property: JsonRequired, MaxLength(200)] string? Platform,
    [property: JsonRequired] DraftDiscount? OrderDiscount,
    [property: JsonRequired, MaxLength(50)] IReadOnlyList<DraftCharge> Charges)
{
    public DraftContentV4(string? title, string? supplierName, string? currency, string? notes,
        IReadOnlyList<string> sourceLinks, IReadOnlyList<DraftEntryV4> entries, Guid? supplierId,
        string? supplierContactName, string? supplierEmail, string? supplierPhone, string? supplierWebsite,
        string? supplierPostalAddress, string? supplierOrderReference, string? platform)
        : this(title, supplierName, currency, notes, sourceLinks, entries, supplierId, supplierContactName,
            supplierEmail, supplierPhone, supplierWebsite, supplierPostalAddress, supplierOrderReference, platform, null, [])
    { }
}


[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDraftOrderRequestV4([property: JsonRequired] Guid RequestId, [property: JsonRequired] DraftContentV4 Draft);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateDraftOrderRequestV4([property: JsonRequired] Guid RequestId, [property: JsonRequired] string ExpectedVersion, [property: JsonRequired] DraftContentV4 Draft);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateDraftOrderRequestV4([property: JsonRequired] DraftContentV4 Draft);
public sealed record DraftOrderResponseV4(Guid Id, DraftContentV4 Draft, string CreatedAtUtc, string UpdatedAtUtc,
    string Version, string PoReference, bool SupplierIsArchived, DraftCalculationResponseV4 Calculation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftLegacyPricingV4(
    [property: JsonRequired] string? Quantity,
    [property: JsonRequired] string? UnitOfMeasure,
    [property: JsonRequired] string? UnitPrice,
    [property: JsonRequired] string? PricingUnit,
    [property: JsonRequired] string? PricePerQuantity,
    [property: JsonRequired] string? PricingQuantity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftDiscount([property: JsonRequired] string Mode, [property: JsonRequired] string Value);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftCharge(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string Category,
    [property: JsonRequired, MaxLength(200)] string Label,
    [property: JsonRequired] string? Amount,
    [property: JsonRequired] string PayeeKind,
    [property: JsonRequired, MaxLength(200)] string? PayeeName,
    [property: JsonRequired] string AmountStatus,
    [property: JsonRequired, MaxLength(200)] string? Reference,
    [property: JsonRequired, MaxLength(2000)] string? Notes);
public sealed record DraftLineCalculationV4(Guid Id, string? Gross, string? DiscountBase, string? DiscountAmount, string? Net);
public sealed record DraftCalculationResponseV4(IReadOnlyList<DraftLineCalculationV4> Lines, int IncompleteLineCount,
    string? MerchandiseEstimate, string? LineDiscountTotal, string? MerchandiseNet, string? OrderDiscountBase,
    string? OrderDiscountAmount, string? DiscountedMerchandise, string? SupplierCharges, string? ThirdPartyCharges,
    string? SupplierEstimate, string? PurchaseEstimate, int IncompleteChargeCount);
