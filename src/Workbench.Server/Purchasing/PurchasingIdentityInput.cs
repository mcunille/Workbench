// Copyright (c) 2026 The White Stag Collection.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Workbench.Server.Purchasing;

public static class PurchasingIdentityInput
{
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static SupplierContent Normalize(SupplierContent input) => input with
    {
        Name = Trim(input.Name)!,
        ContactName = Trim(input.ContactName),
        Email = Trim(input.Email),
        Phone = Trim(input.Phone),
        Website = Trim(input.Website),
        PostalAddress = string.IsNullOrWhiteSpace(input.PostalAddress) ? null : input.PostalAddress
    };
    public static DraftContent Legacy(DraftContentV2 input) => new(input.Title, input.SupplierName, input.Currency, input.Notes, input.SourceLinks, input.Entries);
    public static DraftContentV2 Normalize(DraftContentV2 input)
    {
        var old = DraftOrderInput.Normalize(Legacy(input));
        var contact = Normalize(new SupplierContent(input.SupplierName!, input.SupplierContactName, input.SupplierEmail, input.SupplierPhone, input.SupplierWebsite, input.SupplierPostalAddress));
        return input with
        {
            Title = old.Title,
            SupplierName = contact.Name,
            Currency = old.Currency,
            Notes = old.Notes,
            SourceLinks = old.SourceLinks,
            Entries = old.Entries,
            SupplierContactName = contact.ContactName,
            SupplierEmail = contact.Email,
            SupplierPhone = contact.Phone,
            SupplierWebsite = contact.Website,
            SupplierPostalAddress = contact.PostalAddress,
            SupplierOrderReference = Trim(input.SupplierOrderReference),
            Platform = Trim(input.Platform)
        };
    }
    public static Dictionary<string, string[]> Validate(SupplierContent? input, bool required = true, string prefix = "supplier.")
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) { errors["supplier"] = ["Supply supplier details."]; return errors; }
        void Field(string? value, int max, string key)
        {
            if (value?.Length > max || value?.Any(char.IsControl) == true) errors[prefix + key] = [$"Use at most {max} characters without control characters."];
        }
        Field(input.Name, 200, "name"); Field(input.ContactName, 200, "contactName"); Field(input.Email, 254, "email"); Field(input.Phone, 100, "phone");
        if (required && string.IsNullOrWhiteSpace(input.Name)) errors[prefix + "name"] = ["Enter a supplier name."];
        if (input.Email is { } email && (email.Count(c => c == '@') != 1 || email.StartsWith('@') || email.EndsWith('@') || email.Any(char.IsWhiteSpace) || email.IndexOfAny(['<', '>', ',', ';']) >= 0))
            errors[prefix + "email"] = ["Enter one email address."];
        if (input.Website is { } website && (website.Length > 2048 || website.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || website.Contains('\\') ||
            !Uri.TryCreate(website, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)))
            errors[prefix + "website"] = ["Use an absolute HTTP or HTTPS website without credentials."];
        if (input.PostalAddress?.Length > 2000) errors[prefix + "postalAddress"] = ["Use at most 2000 characters."];
        return errors;
    }
    public static Dictionary<string, string[]> Validate(DraftContentV2? input)
    {
        if (input is null) return new() { ["draft"] = ["Supply a draft."] };
        var errors = DraftOrderInput.Validate(Legacy(input));
        var contact = Validate(new(input.SupplierName!, input.SupplierContactName, input.SupplierEmail, input.SupplierPhone, input.SupplierWebsite, input.SupplierPostalAddress), false);
        foreach (var pair in contact) errors["draft.supplier" + char.ToUpperInvariant(pair.Key[9]) + pair.Key[10..]] = pair.Value;
        if (input.SupplierId == Guid.Empty) errors["draft.supplierId"] = ["Choose an existing supplier."];
        foreach (var field in new[] { (input.Platform, "draft.platform"), (input.SupplierOrderReference, "draft.supplierOrderReference") })
            if (field.Item1?.Length > 200 || field.Item1?.Any(char.IsControl) == true) errors[field.Item2] = ["Use at most 200 characters without control characters."];
        return errors;
    }
    public static string Canonical(string operation, Guid? targetId, string? expectedVersion, DraftContentV2 draft) =>
        JsonSerializer.Serialize(new { operation, targetId, expectedVersion, draft }, DraftOrderInput.JsonOptions);
    public static string? Query(string? query) => Trim(query)?.ToUpperInvariant();
    private static string QueryHash(string? query) => Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(query ?? "")));
    public static string Cursor(DateTimeOffset time, Guid id, string? query) => "v2" + DraftOrderCursor.Encode(time, id)[2..] + "_" + QueryHash(query);
    public static bool Decode(string? cursor, string? query, out DateTimeOffset time, out Guid id)
    {
        time = default; id = default;
        if (cursor is null) return true;
        var index = cursor.LastIndexOf('_');
        return cursor.Length <= 193 && cursor.StartsWith("v2_", StringComparison.Ordinal) && index > 0 && cursor[(index + 1)..] == QueryHash(query) && DraftOrderCursor.TryDecode("v1" + cursor[2..index], out time, out id);
    }
    public static string Reference(long? number) => "PO-" + number.GetValueOrDefault().ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}
