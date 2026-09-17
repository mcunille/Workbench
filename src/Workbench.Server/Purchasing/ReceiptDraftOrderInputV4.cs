// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Workbench.Server.Purchasing;

// Frozen fingerprint-4 normalization for retired HTTP retries. Keep separate from the evolving
// beta input normalizer; remove only under an approved receipt-retention policy.

internal static partial class ReceiptDraftOrderInputV4
{
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,18})(\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PricePattern();
    private static BigInteger Scaled(string value)
    {
        var parts = value.Split('.');
        return BigInteger.Parse(parts[0], CultureInfo.InvariantCulture) * 10000 + (parts.Length == 2 ? BigInteger.Parse(parts[1].PadRight(4, '0'), CultureInfo.InvariantCulture) : 0);
    }
    private static string Format(BigInteger value) => (value / 10000).ToString(CultureInfo.InvariantCulture) + "." + (value % 10000).ToString("D4", CultureInfo.InvariantCulture);
    public static DraftEntryV3 Legacy(ReceiptDraftEntryV4 e) => new(e.Id, e.Description, e.Notes, e.SourceLink, e.IndicativePrice,
        e.Quantity, e.UnitOfMeasure, null, null, null, null, e.SupplierSku, e.ItemType);
    public static DraftContentV3 Legacy(ReceiptDraftContentV4 d) => new(d.Title, d.SupplierName, d.Currency, d.Notes, d.SourceLinks,
        d.Entries?.Select(e => e is null ? null! : Legacy(e)).ToArray()!, d.SupplierId, d.SupplierContactName, d.SupplierEmail,
        d.SupplierPhone, d.SupplierWebsite, d.SupplierPostalAddress, d.SupplierOrderReference, d.Platform);
    public static ReceiptDraftContentV4 Normalize(ReceiptDraftContentV4 input)
    {
        var old = DraftOrderInputV3.Normalize(Legacy(input));
        return input with
        {
            Title = old.Title,
            SupplierName = old.SupplierName,
            Currency = old.Currency,
            Notes = old.Notes,
            SourceLinks = old.SourceLinks,
            SupplierContactName = old.SupplierContactName,
            SupplierEmail = old.SupplierEmail,
            SupplierPhone = old.SupplierPhone,
            SupplierWebsite = old.SupplierWebsite,
            SupplierPostalAddress = old.SupplierPostalAddress,
            SupplierOrderReference = old.SupplierOrderReference,
            Platform = old.Platform,
            Entries = input.Entries?.Select((e, i) => e is null ? null! : e with
            {
                Description = old.Entries[i].Description,
                Notes = old.Entries[i].Notes,
                SourceLink = old.Entries[i].SourceLink,
                IndicativePrice = old.Entries[i].IndicativePrice,
                Quantity = old.Entries[i].Quantity,
                SupplierSku = old.Entries[i].SupplierSku,
                ItemType = old.Entries[i].ItemType,
                LegacyPricing = NormalizeLegacy(e.LegacyPricing),
                Price = e.Price is not null && PricePattern().IsMatch(e.Price) ? Format(Scaled(e.Price)) : e.Price
            }).ToArray()!
        };
    }
    private static ReceiptDraftLegacyPricingV4? NormalizeLegacy(ReceiptDraftLegacyPricingV4? legacy)
    {
        if (legacy is null) return null;
        string? Number(string? value) => value is not null && PricePattern().IsMatch(value) ? Format(Scaled(value)) : value;
        return legacy with { Quantity = Number(legacy.Quantity), UnitPrice = Number(legacy.UnitPrice), PricePerQuantity = Number(legacy.PricePerQuantity), PricingQuantity = Number(legacy.PricingQuantity) };
    }
    public static string Canonical(string operation, Guid? targetId, string? expectedVersion, ReceiptDraftContentV4 draft) => JsonSerializer.Serialize(new { operation, targetId, expectedVersion, draft }, ReceiptDraftOrderInputV1.JsonOptions);
}
