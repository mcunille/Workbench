// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Workbench.Server.Purchasing;

public static partial class DraftOrderInputV3
{
    private static readonly HashSet<string> Units = ["piece", "carat", "gram", "kilogram", "ounce", "troyOunce", "millimeter", "centimeter", "meter", "parcel", "pair", "set", "pack", "box", "lot"];
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,8})(\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPattern();
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,14})(\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PricePattern();
    private static string? Number(string? value, Regex pattern) => value is not null && pattern.IsMatch(value)
        ? decimal.Parse(value, CultureInfo.InvariantCulture).ToString("F4", CultureInfo.InvariantCulture) : value;
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static DraftEntry Legacy(DraftEntryV3 entry) => new(entry.Id, entry.Description, entry.Notes, entry.SourceLink, entry.IndicativePrice);
    public static DraftEntryV3 Upgrade(DraftEntry entry) => new(entry.Id, entry.Description, entry.Notes, entry.SourceLink, entry.IndicativePrice, null, null, null, null, null, null, null, null);
    public static DraftContentV2 Legacy(DraftContentV3 input) => new(input.Title, input.SupplierName, input.Currency, input.Notes, input.SourceLinks,
        input.Entries?.Select(e => e is null ? null! : Legacy(e)).ToArray()!, input.SupplierId, input.SupplierContactName, input.SupplierEmail,
        input.SupplierPhone, input.SupplierWebsite, input.SupplierPostalAddress, input.SupplierOrderReference, input.Platform);
    public static DraftContentV3 Normalize(DraftContentV3 input)
    {
        var old = PurchasingIdentityInput.Normalize(Legacy(input));
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
                Quantity = Number(e.Quantity, QuantityPattern()),
                UnitPrice = Number(e.UnitPrice, PricePattern()),
                PricePerQuantity = Number(e.PricePerQuantity, QuantityPattern()),
                PricingQuantity = Number(e.PricingQuantity, QuantityPattern()),
                SupplierSku = Trim(e.SupplierSku),
                ItemType = Trim(e.ItemType)
            }).ToArray()!
        };
    }
    public static Dictionary<string, string[]> Validate(DraftContentV3? input)
    {
        if (input is null) return new() { ["draft"] = ["Supply a draft."] };
        var errors = PurchasingIdentityInput.Validate(Legacy(input));
        if (input.Entries is not null)
            for (var i = 0; i < Math.Min(input.Entries.Count, 100); i++)
            {
                var e = input.Entries[i]; if (e is null) continue;
                var field = $"draft.entries[{i}]";
                foreach (var (value, name) in new[] { (e.Quantity, "quantity"), (e.PricePerQuantity, "pricePerQuantity"), (e.PricingQuantity, "pricingQuantity") })
                    if (value is not null && (!QuantityPattern().IsMatch(value) || Scaled(value) == 0))
                        errors[field + "." + name] = ["Use a positive decimal with up to nine integer digits and four decimal places."];
                foreach (var (value, name) in new[] { (e.UnitOfMeasure, "unitOfMeasure"), (e.PricingUnit, "pricingUnit") })
                    if (value is not null && !Units.Contains(value)) errors[field + "." + name] = ["Choose a supported unit."];
                if (e.UnitPrice is not null)
                {
                    if (!PricePattern().IsMatch(e.UnitPrice)) errors[field + ".unitPrice"] = ["Use a nonnegative decimal amount with up to 15 integer digits and four decimal places."];
                    if (input.Currency is null) errors["draft.currency"] = ["Choose a currency when entering a price."];
                    if (e.IndicativePrice is not null) errors[field + ".unitPrice"] = ["Clear the reference price before entering a unit price."];
                }
                if (e.PricingQuantity is not null && (e.UnitOfMeasure is null || e.PricingUnit is null || e.UnitOfMeasure == e.PricingUnit))
                    errors[field + ".pricingQuantity"] = ["A separate pricing quantity requires two different, known units."];
                if (e.SupplierSku?.Length > 200) errors[field + ".supplierSku"] = ["Use at most 200 characters."];
                if (e.ItemType?.Length > 100) errors[field + ".itemType"] = ["Use at most 100 characters."];
                if (!errors.Keys.Any(key => key.StartsWith(field, StringComparison.Ordinal)) && Gross(e) is { } gross && gross > BigInteger.Pow(10, 23) - 1)
                    errors[field + ".unitPrice"] = ["The line estimate exceeds 19 integer digits. Reduce its quantity or price."];
            }
        if (errors.Count == 0 && Encoding.Unicode.GetByteCount(ContentJson(input)) > DraftOrderInput.MaximumContentBytes)
            errors["draft"] = ["The draft is too large to save. Shorten its text or remove entries."];
        return errors;
    }
    private static BigInteger Scaled(string value)
    {
        var parts = value.Split('.');
        return BigInteger.Parse(parts[0], CultureInfo.InvariantCulture) * 10000 + (parts.Length == 2 ? BigInteger.Parse(parts[1].PadRight(4, '0'), CultureInfo.InvariantCulture) : 0);
    }
    private static BigInteger? Gross(DraftEntryV3 entry)
    {
        if (entry.Quantity is null || entry.UnitOfMeasure is null || entry.UnitPrice is null || entry.PricingUnit is null || entry.PricePerQuantity is null) return null;
        var quantity = entry.UnitOfMeasure == entry.PricingUnit ? entry.Quantity : entry.PricingQuantity;
        if (quantity is null) return null;
        // Each operand has scale 10,000; product / denominator therefore already has the
        // desired four-place output scale. Round its exact remainder only once.
        var denominator = Scaled(entry.PricePerQuantity);
        var rounded = BigInteger.DivRem(Scaled(quantity) * Scaled(entry.UnitPrice), denominator, out var remainder);
        return rounded + (remainder * 2 >= denominator ? 1 : 0);
    }
    private static string Format(BigInteger value) => (value / 10000).ToString(CultureInfo.InvariantCulture) + "." + (value % 10000).ToString("D4", CultureInfo.InvariantCulture);
    public static DraftCalculationResponse Calculate(DraftContentV3 draft)
    {
        var lines = draft.Entries.Select(e => (e.Id, Gross: Gross(e))).ToArray();
        var known = lines.Where(e => e.Gross is not null).ToArray();
        return new(lines.Select(e => new DraftLineCalculation(e.Id, e.Gross is { } gross ? Format(gross) : null)).ToArray(),
            lines.Length - known.Length, known.Length == 0 ? null : Format(known.Aggregate(BigInteger.Zero, (sum, e) => sum + e.Gross!.Value)));
    }
    public static string ContentJson(DraftContentV3 draft) => JsonSerializer.Serialize(new { draft.SourceLinks, draft.Entries }, DraftOrderInput.JsonOptions);
    public static string Canonical(string operation, Guid? targetId, string? expectedVersion, DraftContentV3 draft) => JsonSerializer.Serialize(new { operation, targetId, expectedVersion, draft }, DraftOrderInput.JsonOptions);
    public static DraftEntryV3[] ReadEntries(JsonElement content, int schemaVersion) => schemaVersion == 1
        ? content.GetProperty("entries").Deserialize<DraftEntry[]>(DraftOrderInput.JsonOptions)!.Select(Upgrade).ToArray()
        : content.GetProperty("entries").Deserialize<DraftEntryV3[]>(DraftOrderInput.JsonOptions)!;
}
