// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Workbench.Server.Purchasing;

public static partial class DraftOrderInput
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const int MaximumContentBytes = 1024 * 1024;
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,18})(\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PricePattern();
    private static BigInteger Scaled(string value)
    {
        var parts = value.Split('.');
        return BigInteger.Parse(parts[0], CultureInfo.InvariantCulture) * 10000 + (parts.Length == 2 ? BigInteger.Parse(parts[1].PadRight(4, '0'), CultureInfo.InvariantCulture) : 0);
    }
    private static string Format(BigInteger value) => (value / 10000).ToString(CultureInfo.InvariantCulture) + "." + (value % 10000).ToString("D4", CultureInfo.InvariantCulture);
    public static DraftContent Normalize(DraftContent input) => input with
    {
        Title = Trim(input.Title),
        SupplierName = Trim(input.SupplierName),
        Currency = string.IsNullOrWhiteSpace(input.Currency) ? null : input.Currency.ToUpperInvariant(),
        Notes = Notes(input.Notes),
        SourceLinks = input.SourceLinks?.Select(link => link?.Trim()!).ToArray()!,
        SupplierContactName = Trim(input.SupplierContactName),
        SupplierEmail = Trim(input.SupplierEmail),
        SupplierPhone = Trim(input.SupplierPhone),
        SupplierWebsite = Trim(input.SupplierWebsite),
        SupplierPostalAddress = Notes(input.SupplierPostalAddress),
        SupplierOrderReference = Trim(input.SupplierOrderReference),
        Platform = Trim(input.Platform),
        OrderDiscount = NormalizeDiscount(input.OrderDiscount),
        Charges = input.Charges?.Select(c => c is null ? null! : c with { Label = c.Label?.Trim()!, Amount = Number(c.Amount), PayeeName = Trim(c.PayeeName), Reference = Trim(c.Reference), Notes = Trim(c.Notes) }).ToArray()!,
        Entries = input.Entries?.Select(e => e is null ? null! : e with
        {
            Description = Trim(e.Description),
            Notes = Notes(e.Notes),
            SourceLink = Trim(e.SourceLink),
            IndicativePrice = Number(e.IndicativePrice, ReferencePricePattern()),
            Quantity = Number(e.Quantity, QuantityPattern()),
            SupplierSku = Trim(e.SupplierSku),
            ItemType = Trim(e.ItemType),
            LegacyPricing = NormalizeLegacy(e.LegacyPricing),
            Discount = NormalizeDiscount(e.Discount),
            Price = e.Price is not null && PricePattern().IsMatch(e.Price) ? Format(Scaled(e.Price)) : e.Price
        }).ToArray()!
    };
    private static DraftLegacyPricing? NormalizeLegacy(DraftLegacyPricing? legacy)
    {
        if (legacy is null) return null;
        string? Number(string? value) => value is not null && PricePattern().IsMatch(value) ? Format(Scaled(value)) : value;
        return legacy with { Quantity = Number(legacy.Quantity), UnitPrice = Number(legacy.UnitPrice), PricePerQuantity = Number(legacy.PricePerQuantity), PricingQuantity = Number(legacy.PricingQuantity) };
    }
    public static Dictionary<string, string[]> Validate(DraftContent? input)
    {
        if (input is null) return new() { ["draft"] = ["Supply a draft."] };
        var errors = ValidateCommon(input);
        if (input.Entries is not null)
            for (var i = 0; i < Math.Min(input.Entries.Count, 100); i++)
            {
                var e = input.Entries[i]; if (e is null) continue;
                var field = $"draft.entries[{i}]";
                if (e.PriceMode is not ("perUnit" or "lineTotal")) errors[field + ".priceMode"] = ["Choose per unit or total line pricing."];
                if (e.Price is not null)
                {
                    if (!PricePattern().IsMatch(e.Price) || (e.PriceMode == "perUnit" && Scaled(e.Price) >= BigInteger.Pow(10, 19)))
                        errors[field + ".price"] = ["Use a nonnegative price with up to four decimal places (15 integer digits per unit, 19 for a line total)."];
                    if (input.Currency is null) errors["draft.currency"] = ["Choose a currency when entering a price."];
                    if (e.IndicativePrice is not null || e.LegacyPricing is not null) errors[field + ".price"] = ["Resolve the previous quote before entering a price."];
                }
                if (e.LegacyPricing is { } legacy)
                {
                    if (legacy.UnitPrice is not null && input.Currency is null)
                        errors["draft.currency"] = ["Choose a currency when entering a price."];
                    if (!ValidRetainedQuote(legacy, e.IndicativePrice))
                        errors[field + ".legacyPricing"] = ["The retained quote is invalid."];
                }
                if (!errors.Keys.Any(key => key.StartsWith(field, StringComparison.Ordinal)) && Gross(e) is { } gross && gross >= BigInteger.Pow(10, 23))
                    errors[field + ".price"] = ["The line estimate exceeds 19 integer digits."];
            }
        ValidateAdjustments(input, errors);
        if (errors.Count == 0 && Encoding.Unicode.GetByteCount(ContentJson(input)) > MaximumContentBytes)
            errors["draft"] = ["The draft is too large to save. Shorten its text or remove entries."];
        return errors;
    }
    private static BigInteger? Gross(DraftEntry e)
    {
        if (e.Price is null || e.IndicativePrice is not null || e.LegacyPricing is not null) return null;
        if (e.PriceMode == "lineTotal") return Scaled(e.Price);
        if (e.Quantity is null || e.UnitOfMeasure is null) return null;
        var result = BigInteger.DivRem(Scaled(e.Quantity) * Scaled(e.Price), 10000, out var remainder);
        return result + (remainder >= 5000 ? 1 : 0);
    }
    internal static DraftEntry Upgrade(StoredDraftEntry e)
    {
        var result = new DraftEntry(e.Id, e.Description, e.Notes, e.SourceLink, e.IndicativePrice, e.Quantity, e.UnitOfMeasure, "perUnit", null, null, e.SupplierSku, e.ItemType);
        var retained = new DraftLegacyPricing(e.Quantity, e.UnitOfMeasure, e.UnitPrice, e.PricingUnit, e.PricePerQuantity, e.PricingQuantity);
        var gross = RetainedGross(retained) is { } total ? Format(total) : null;
        if (gross is not null)
        {
            var quantity = e.UnitOfMeasure == e.PricingUnit ? e.Quantity : e.PricingQuantity;
            var unitPrice = BigInteger.DivRem(Scaled(e.UnitPrice!) * 10000, Scaled(e.PricePerQuantity!), out var remainder);
            result = result with { Quantity = quantity, UnitOfMeasure = e.PricingUnit };
            if (remainder == 0 && unitPrice < BigInteger.Pow(10, 19))
            {
                var candidate = result with { Price = Format(unitPrice) };
                if (Gross(candidate) == Scaled(gross)) return candidate;
            }
            return result with { PriceMode = "lineTotal", Price = gross };
        }
        if (e.UnitPrice is not null || e.PricingUnit is not null || e.PricePerQuantity is not null || e.PricingQuantity is not null)
            result = result with { LegacyPricing = new(e.Quantity, e.UnitOfMeasure, e.UnitPrice, e.PricingUnit, e.PricePerQuantity, e.PricingQuantity) };
        return result;
    }
    public static DraftEntry[] ReadEntries(JsonElement content, int schemaVersion)
    {
        if (schemaVersion >= 3)
        {
            var entries = System.Text.Json.Nodes.JsonNode.Parse(content.GetProperty("entries").GetRawText())!;
            if (schemaVersion == 3) foreach (var entry in entries.AsArray()) entry!["discount"] = null;
            return entries.Deserialize<DraftEntry[]>(JsonOptions)!;
        }
        return content.GetProperty("entries").Deserialize<StoredDraftEntry[]>(JsonOptions)!.Select(Upgrade).ToArray();
    }
    public static string ContentJson(DraftContent draft) => JsonSerializer.Serialize(new { draft.SourceLinks, draft.Entries, draft.OrderDiscount, draft.Charges }, JsonOptions);
    public static string Canonical(string operation, Guid? targetId, string? expectedVersion, DraftContent draft) => JsonSerializer.Serialize(new { operation, targetId, expectedVersion, draft }, JsonOptions);
}
