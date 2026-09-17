// Copyright (c) 2026 The White Stag Collection.
using System.Security.Cryptography;
using System.Text;
namespace Workbench.Server.Purchasing;

internal static class PurchasingIdentityInput
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
