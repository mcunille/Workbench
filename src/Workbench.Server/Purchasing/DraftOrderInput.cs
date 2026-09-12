// Copyright (c) 2026 The White Stag Collection.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Workbench.Server.Purchasing;

public static partial class DraftOrderInput
{
    public const int MaximumContentBytes = 1024 * 1024;
    // Fingerprint V1 is pinned to these serialization defaults and declaration order.
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,14})(\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PricePattern();

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Notes(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static DraftContent Normalize(DraftContent input) => input with
    {
        Title = Trim(input.Title),
        SupplierName = Trim(input.SupplierName),
        Currency = string.IsNullOrWhiteSpace(input.Currency) ? null : input.Currency.ToUpperInvariant(),
        Notes = Notes(input.Notes),
        SourceLinks = input.SourceLinks?.Select(link => link?.Trim()!).ToArray()!,
        Entries = input.Entries?.Select(entry => entry is null ? null! : entry with
        {
            Description = Trim(entry.Description),
            Notes = Notes(entry.Notes),
            SourceLink = Trim(entry.SourceLink),
            IndicativePrice = entry.IndicativePrice is { } price && PricePattern().IsMatch(price)
                ? decimal.Parse(price, CultureInfo.InvariantCulture).ToString("F4", CultureInfo.InvariantCulture) : entry.IndicativePrice,
        }).ToArray()!,
    };

    public static Dictionary<string, string[]> Validate(DraftContent? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) { errors["draft"] = ["A draft object is required."]; return errors; }
        void Length(string? value, int limit, string field)
        {
            if (value?.Length > limit) errors[field] = [$"Use at most {limit} characters."];
        }
        void Link(string? value, string field)
        {
            if (value is null || value.Length > 2048 || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                errors[field] = ["Use an absolute HTTP or HTTPS link without embedded credentials, up to 2048 characters."];
        }
        Length(input.Title, 200, "draft.title");
        Length(input.SupplierName, 200, "draft.supplierName");
        Length(input.Notes, 10000, "draft.notes");
        if (input.Currency is not null && (input.Currency.Length != 3 || input.Currency.Any(c => c is < 'A' or > 'Z')))
            errors["draft.currency"] = ["Use exactly three currency letters."];
        if (input.SourceLinks is null || input.SourceLinks.Count > 20) errors["draft.sourceLinks"] = ["Supply an array of up to 20 source links."];
        if (input.Entries is null || input.Entries.Count > 100) errors["draft.entries"] = ["Supply an array of up to 100 shopping-list entries."];
        if (input.SourceLinks is not null)
            for (var index = 0; index < Math.Min(input.SourceLinks.Count, 20); index++) Link(input.SourceLinks[index], $"draft.sourceLinks[{index}]");
        var ids = new HashSet<Guid>();
        if (input.Entries is not null)
            for (var index = 0; index < Math.Min(input.Entries.Count, 100); index++)
            {
                var field = $"draft.entries[{index}]";
                var entry = input.Entries[index];
                if (entry is null) { errors[field] = ["An entry object is required."]; continue; }
                if (entry.Id == Guid.Empty || !ids.Add(entry.Id)) errors[field + ".id"] = ["Use a unique, nonempty entry identifier."];
                Length(entry.Description, 500, field + ".description");
                Length(entry.Notes, 2000, field + ".notes");
                if (entry.SourceLink is not null) Link(entry.SourceLink, field + ".sourceLink");
                if (entry.IndicativePrice is not null)
                {
                    if (!PricePattern().IsMatch(entry.IndicativePrice)) errors[field + ".indicativePrice"] = ["Use a nonnegative decimal amount with up to 15 integer digits and four decimal places."];
                    if (input.Currency is null) errors["draft.currency"] = ["Choose a currency when entering a price."];
                }
            }
        if (errors.Count == 0 && Encoding.Unicode.GetByteCount(ContentJson(input)) > MaximumContentBytes)
            errors["draft"] = ["The draft is too large to save. Shorten its text or remove entries."];
        return errors;
    }

    public static string ContentJson(DraftContent draft) => JsonSerializer.Serialize(new { draft.SourceLinks, draft.Entries }, JsonOptions);

    public static string Canonical(string operation, Guid? targetId, string? expectedVersion, DraftContent draft) =>
        JsonSerializer.Serialize(new { operation, targetId, expectedVersion, draft }, JsonOptions);

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
