// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Workbench.Server.Purchasing;

public static partial class DraftOrderInputV4
{
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,18})(\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PricePattern();
    private static BigInteger Scaled(string value)
    {
        var parts = value.Split('.');
        return BigInteger.Parse(parts[0], CultureInfo.InvariantCulture) * 10000 + (parts.Length == 2 ? BigInteger.Parse(parts[1].PadRight(4, '0'), CultureInfo.InvariantCulture) : 0);
    }
    private static string Format(BigInteger value) => (value / 10000).ToString(CultureInfo.InvariantCulture) + "." + (value % 10000).ToString("D4", CultureInfo.InvariantCulture);
    public static DraftEntryV3 Legacy(DraftEntryV4 e) => new(e.Id, e.Description, e.Notes, e.SourceLink, e.IndicativePrice,
        e.Quantity, e.UnitOfMeasure, null, null, null, null, e.SupplierSku, e.ItemType);
    public static DraftContentV3 Legacy(DraftContentV4 d) => new(d.Title, d.SupplierName, d.Currency, d.Notes, d.SourceLinks,
        d.Entries?.Select(e => e is null ? null! : Legacy(e)).ToArray()!, d.SupplierId, d.SupplierContactName, d.SupplierEmail,
        d.SupplierPhone, d.SupplierWebsite, d.SupplierPostalAddress, d.SupplierOrderReference, d.Platform);
    public static DraftContentV4 Normalize(DraftContentV4 input)
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
    private static DraftLegacyPricingV4? NormalizeLegacy(DraftLegacyPricingV4? legacy)
    {
        if (legacy is null) return null;
        string? Number(string? value) => value is not null && PricePattern().IsMatch(value) ? Format(Scaled(value)) : value;
        return legacy with { Quantity = Number(legacy.Quantity), UnitPrice = Number(legacy.UnitPrice), PricePerQuantity = Number(legacy.PricePerQuantity), PricingQuantity = Number(legacy.PricingQuantity) };
    }
    public static Dictionary<string, string[]> Validate(DraftContentV4? input)
    {
        if (input is null) return new() { ["draft"] = ["Supply a draft."] };
        var errors = DraftOrderInputV3.Validate(Legacy(input));
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
                    var original = new DraftEntryV3(e.Id, null, null, null, e.IndicativePrice,
                        legacy.Quantity, legacy.UnitOfMeasure, legacy.UnitPrice, legacy.PricingUnit,
                        legacy.PricePerQuantity, legacy.PricingQuantity, null, null);
                    var quote = new DraftContentV3(null, null, input.Currency, null, [], [original],
                        null, null, null, null, null, null, null, null);
                    var quoteErrors = DraftOrderInputV3.Validate(quote);
                    if (quoteErrors.TryGetValue("draft.currency", out var currencyErrors))
                        errors["draft.currency"] = currencyErrors;
                    if (new[] { "quantity", "unitOfMeasure", "unitPrice", "pricingUnit", "pricePerQuantity", "pricingQuantity" }
                        .Any(name => quoteErrors.ContainsKey("draft.entries[0]." + name)))
                        errors[field + ".legacyPricing"] = ["The retained quote is invalid."];
                }
                if (!errors.Keys.Any(key => key.StartsWith(field, StringComparison.Ordinal)) && Gross(e) is { } gross && gross >= BigInteger.Pow(10, 23))
                    errors[field + ".price"] = ["The line estimate exceeds 19 integer digits."];
            }
        if (errors.Count == 0 && Encoding.Unicode.GetByteCount(ContentJson(input)) > DraftOrderInput.MaximumContentBytes)
            errors["draft"] = ["The draft is too large to save. Shorten its text or remove entries."];
        return errors;
    }
    private static BigInteger? Gross(DraftEntryV4 e)
    {
        if (e.Price is null || e.IndicativePrice is not null || e.LegacyPricing is not null) return null;
        if (e.PriceMode == "lineTotal") return Scaled(e.Price);
        if (e.Quantity is null || e.UnitOfMeasure is null) return null;
        var result = BigInteger.DivRem(Scaled(e.Quantity) * Scaled(e.Price), 10000, out var remainder);
        return result + (remainder >= 5000 ? 1 : 0);
    }
    public static DraftCalculationResponse Calculate(DraftContentV4 draft)
    {
        var lines = draft.Entries.Select(e => (e.Id, Gross: Gross(e))).ToArray();
        var known = lines.Where(e => e.Gross is not null).ToArray();
        return new(lines.Select(e => new DraftLineCalculation(e.Id, e.Gross is { } gross ? Format(gross) : null)).ToArray(),
            lines.Length - known.Length, known.Length == 0 ? null : Format(known.Aggregate(BigInteger.Zero, (sum, e) => sum + e.Gross!.Value)));
    }
    public static DraftEntryV4 Upgrade(DraftEntryV3 e)
    {
        var result = new DraftEntryV4(e.Id, e.Description, e.Notes, e.SourceLink, e.IndicativePrice, e.Quantity, e.UnitOfMeasure, "perUnit", null, null, e.SupplierSku, e.ItemType);
        var empty = new DraftContentV3(null, null, "USD", null, [], [e], null, null, null, null, null, null, null, null);
        var gross = DraftOrderInputV3.Calculate(empty).Lines[0].Gross;
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
    public static DraftEntryV4[] ReadEntries(JsonElement content, int schemaVersion) => schemaVersion == 3
        ? content.GetProperty("entries").Deserialize<DraftEntryV4[]>(DraftOrderInput.JsonOptions)!
        : DraftOrderInputV3.ReadEntries(content, schemaVersion).Select(Upgrade).ToArray();
    public static string ContentJson(DraftContentV4 draft) => JsonSerializer.Serialize(new { draft.SourceLinks, draft.Entries }, DraftOrderInput.JsonOptions);
    public static string Canonical(string operation, Guid? targetId, string? expectedVersion, DraftContentV4 draft) => JsonSerializer.Serialize(new { operation, targetId, expectedVersion, draft }, DraftOrderInput.JsonOptions);
}
