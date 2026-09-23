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
        Website = NormalizeWebsite(input.Website),
        SocialProfiles = input.SocialProfiles is { Count: > 0 } profiles ? profiles.Select(profile => profile is null ? null! : new SupplierSocialProfile(profile.Label?.Trim()!, profile.Handle?.Trim()!)).ToArray() : null,
        PostalAddress = string.IsNullOrWhiteSpace(input.PostalAddress) ? null : input.PostalAddress
    };
    private static string? NormalizeWebsite(string? input)
    {
        var website = Trim(input);
        if (website is null) return null;
        // Preserve explicit schemes, including unsupported ones, for authoritative validation below.
        var colon = website.IndexOf(':');
        if (colon >= 0 && Uri.CheckSchemeName(website[..colon])) return website;
        var candidate = "https://" + website;
        return !website.StartsWith('/') && Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Host.Contains('.') ? candidate : website;
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
            errors[prefix + "website"] = ["Enter a valid HTTP or HTTPS website without spaces or credentials."];
        if (input.PostalAddress?.Length > 2000) errors[prefix + "postalAddress"] = ["Use at most 2000 characters."];
        if (input.SocialProfiles is { } profiles)
        {
            if (profiles.Count > 20) errors[prefix + "socialProfiles"] = ["Use at most 20 social handles."];
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < profiles.Count; index++)
            {
                var profile = profiles[index];
                var key = $"socialProfiles[{index}]";
                Field(profile?.Label, 100, key + ".label");
                Field(profile?.Handle, 2048, key + ".handle");
                if (string.IsNullOrWhiteSpace(profile?.Label)) errors[prefix + key + ".label"] = ["Enter a platform or label."];
                else if (!labels.Add(profile.Label.Trim())) errors[prefix + key + ".label"] = ["Use each platform or label only once."];
                if (string.IsNullOrWhiteSpace(profile?.Handle)) errors[prefix + key + ".handle"] = ["Enter a handle or reference."];
            }
        }
        return errors;
    }
    public static string? Query(string? query) => Trim(query)?.ToUpperInvariant();
    private static string QueryHash(string? query) => Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(query ?? "")));
    public static string SupplierCursor(string name, Guid id, string binding) =>
        "sn1_" + Convert.ToBase64String(Encoding.Unicode.GetBytes(name)) + "_" + id.ToString("N") + "_" + QueryHash(binding);
    public static bool DecodeSupplierCursor(string? cursor, string binding, out string name, out Guid id)
    {
        name = ""; id = default;
        if (cursor is null) return true;
        if (cursor.Length > 640) return false;
        var parts = cursor.Split('_');
        if (parts.Length != 4 || parts[0] != "sn1" || parts[3] != QueryHash(binding)
            || !Guid.TryParseExact(parts[2], "N", out id) || id == Guid.Empty) return false;
        try
        {
            name = new UnicodeEncoding(false, false, true).GetString(Convert.FromBase64String(parts[1]));
            return name.Length <= 200 && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { return false; }
    }
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
