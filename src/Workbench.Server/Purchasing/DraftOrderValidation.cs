// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
namespace Workbench.Server.Purchasing;

public static partial class DraftOrderInput
{
    private static readonly HashSet<string> Units = ["piece", "carat", "gram", "kilogram", "ounce", "troyOunce", "millimeter", "centimeter", "meter", "parcel", "pair", "set", "pack", "box", "lot"];
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,8})(\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPattern();
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,14})(\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePricePattern();
    private static string? Number(string? value, Regex pattern) => value is not null && pattern.IsMatch(value)
        ? decimal.Parse(value, CultureInfo.InvariantCulture).ToString("F4", CultureInfo.InvariantCulture) : value;
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Notes(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static Dictionary<string, string[]> ValidateCommon(DraftContent input)
    {
        var errors = ValidateBasic(input);
        ValidateSupplier(input, errors);
        ValidateOrderMetadata(input, errors);
        ValidateEntryDetails(input, errors);
        return errors;
    }
    private static void ValidateSupplier(DraftContent input, Dictionary<string, string[]> errors)
    {
        var contact = PurchasingIdentityInput.Validate(new(input.SupplierName!, input.SupplierContactName, input.SupplierEmail, input.SupplierPhone, input.SupplierWebsite, input.SupplierPostalAddress), false);
        foreach (var pair in contact) errors["draft.supplier" + char.ToUpperInvariant(pair.Key[9]) + pair.Key[10..]] = pair.Value;
        if (input.SupplierId == Guid.Empty) errors["draft.supplierId"] = ["Choose an existing supplier."];
    }
    private static void ValidateOrderMetadata(DraftContent input, Dictionary<string, string[]> errors)
    {
        foreach (var field in new[] { (input.Platform, "draft.platform"), (input.SupplierOrderReference, "draft.supplierOrderReference") })
            if (field.Item1?.Length > 200 || field.Item1?.Any(char.IsControl) == true) errors[field.Item2] = ["Use at most 200 characters without control characters."];
    }
    private static void ValidateEntryDetails(DraftContent input, Dictionary<string, string[]> errors)
    {
        if (input.Entries is null) return;
        for (var i = 0; i < Math.Min(input.Entries.Count, 100); i++)
        {
            var entry = input.Entries[i];
            if (entry is null) continue;
            var field = $"draft.entries[{i}]";
            if (entry.Quantity is not null && (!QuantityPattern().IsMatch(entry.Quantity) || Scaled(entry.Quantity) == 0))
                errors[field + ".quantity"] = ["Use a positive decimal with up to nine integer digits and four decimal places."];
            if (entry.UnitOfMeasure is not null && !Units.Contains(entry.UnitOfMeasure)) errors[field + ".unitOfMeasure"] = ["Choose a supported unit."];
            if (entry.SupplierSku?.Length > 200) errors[field + ".supplierSku"] = ["Use at most 200 characters."];
            if (entry.ItemType?.Length > 100) errors[field + ".itemType"] = ["Use at most 100 characters."];
        }
    }
    private static bool ValidRetainedQuote(DraftLegacyPricing quote, string? indicativePrice)
    {
        foreach (var value in new[] { quote.Quantity, quote.PricePerQuantity, quote.PricingQuantity })
            if (value is not null && (!QuantityPattern().IsMatch(value) || Scaled(value) == 0)) return false;
        foreach (var unit in new[] { quote.UnitOfMeasure, quote.PricingUnit })
            if (unit is not null && !Units.Contains(unit)) return false;
        if (quote.UnitPrice is not null && (!ReferencePricePattern().IsMatch(quote.UnitPrice) || indicativePrice is not null)) return false;
        if (quote.PricingQuantity is not null && (quote.UnitOfMeasure is null || quote.PricingUnit is null || quote.UnitOfMeasure == quote.PricingUnit)) return false;
        return RetainedGross(quote) is not { } gross || gross < BigInteger.Pow(10, 23);
    }
    private static BigInteger? RetainedGross(DraftLegacyPricing quote)
    {
        if (quote.Quantity is null || quote.UnitOfMeasure is null || quote.UnitPrice is null || quote.PricingUnit is null || quote.PricePerQuantity is null) return null;
        var quantity = quote.UnitOfMeasure == quote.PricingUnit ? quote.Quantity : quote.PricingQuantity;
        if (quantity is null) return null;
        var denominator = Scaled(quote.PricePerQuantity);
        var rounded = BigInteger.DivRem(Scaled(quantity) * Scaled(quote.UnitPrice), denominator, out var remainder);
        return rounded + (remainder * 2 >= denominator ? 1 : 0);
    }
    private static Dictionary<string, string[]> ValidateBasic(DraftContent? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) { errors["draft"] = ["A draft object is required."]; return errors; }
        ValidateDraftFields(input, errors);
        ValidateSourceLinks(input, errors);
        ValidateBasicEntries(input, errors);
        if (errors.Count == 0 && Encoding.Unicode.GetByteCount(ContentJson(input)) > MaximumContentBytes)
            errors["draft"] = ["The draft is too large to save. Shorten its text or remove entries."];
        return errors;
    }
    private static void ValidateLength(string? value, int limit, string field, Dictionary<string, string[]> errors)
    {
        if (value?.Length > limit) errors[field] = [$"Use at most {limit} characters."];
    }
    private static void ValidateLink(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null || value.Length > 2048 || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            errors[field] = ["Use an absolute HTTP or HTTPS link without embedded credentials, up to 2048 characters."];
    }
    private static void ValidateDraftFields(DraftContent input, Dictionary<string, string[]> errors)
    {
        ValidateLength(input.Title, 200, "draft.title", errors);
        ValidateLength(input.SupplierName, 200, "draft.supplierName", errors);
        ValidateLength(input.Notes, 10000, "draft.notes", errors);
        if (input.Currency is not null && (input.Currency.Length != 3 || input.Currency.Any(c => c is < 'A' or > 'Z')))
            errors["draft.currency"] = ["Use exactly three currency letters."];
        if (input.SourceLinks is null || input.SourceLinks.Count > 20) errors["draft.sourceLinks"] = ["Supply an array of up to 20 source links."];
        if (input.Entries is null || input.Entries.Count > 100) errors["draft.entries"] = ["Supply an array of up to 100 shopping-list entries."];
    }
    private static void ValidateSourceLinks(DraftContent input, Dictionary<string, string[]> errors)
    {
        if (input.SourceLinks is null) return;
        for (var index = 0; index < Math.Min(input.SourceLinks.Count, 20); index++)
            ValidateLink(input.SourceLinks[index], $"draft.sourceLinks[{index}]", errors);
    }
    private static void ValidateBasicEntries(DraftContent input, Dictionary<string, string[]> errors)
    {
        if (input.Entries is null) return;
        var ids = new HashSet<Guid>();
        for (var index = 0; index < Math.Min(input.Entries.Count, 100); index++)
        {
            var field = $"draft.entries[{index}]";
            var entry = input.Entries[index];
            if (entry is null) { errors[field] = ["An entry object is required."]; continue; }
            if (entry.Id == Guid.Empty || !ids.Add(entry.Id)) errors[field + ".id"] = ["Use a unique, nonempty entry identifier."];
            ValidateLength(entry.Description, 500, field + ".description", errors);
            ValidateLength(entry.Notes, 2000, field + ".notes", errors);
            if (entry.SourceLink is not null) ValidateLink(entry.SourceLink, field + ".sourceLink", errors);
            if (entry.IndicativePrice is not null)
            {
                if (!ReferencePricePattern().IsMatch(entry.IndicativePrice)) errors[field + ".indicativePrice"] = ["Use a nonnegative decimal amount with up to 15 integer digits and four decimal places."];
                if (input.Currency is null) errors["draft.currency"] = ["Choose a currency when entering a price."];
            }
        }
    }

    public static string? NormalizeVersion(string? input, Dictionary<string, string[]> errors)
    {
        Span<byte> bytes = stackalloc byte[8];
        if (input is null || input.Length != 12 || !Convert.TryFromBase64String(input, bytes, out var count) || count != 8)
        {
            errors["expectedVersion"] = ["A valid saved version is required."];
            return null;
        }
        return Convert.ToBase64String(bytes);
    }
}
