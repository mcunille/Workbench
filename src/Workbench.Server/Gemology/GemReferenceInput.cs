// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Workbench.Server.Gemology;

public static class GemReferenceInput
{
    public static readonly IReadOnlySet<string> MaterialKinds = new HashSet<string>(StringComparer.Ordinal)
        { "mineral", "mineraloid", "organic", "rockAggregate" };
    public static readonly IReadOnlySet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
        { "materialKind", "commonName", "aliases", "group", "species", "variety", "description", "notableLocality" };

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    internal static string Comparison(string? value) => Regex.Replace(
        (value ?? "").Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim().ToUpperInvariant();

    public static GemReferenceContent Normalize(GemReferenceContent content) => content with
    {
        MaterialKind = Text(content.MaterialKind) ?? "",
        CommonName = Text(content.CommonName) ?? "",
        Group = Text(content.Group),
        Species = Text(content.Species),
        Variety = Text(content.Variety),
        Description = Text(content.Description),
        RetirementExplanation = Text(content.RetirementExplanation),
        Aliases = content.Aliases.Select(value => Text(value) ?? "").ToArray(),
        Sources = content.Sources.Select(source => source with
        {
            Title = Text(source.Title) ?? "",
            Publisher = Text(source.Publisher) ?? "",
            Url = Text(source.Url),
            Citation = Text(source.Citation),
        }).ToArray(),
        NotableLocality = content.NotableLocality is { } locality
            ? locality with { Place = Text(locality.Place) ?? "", Scope = Text(locality.Scope) ?? "" } : null,
    };

    public static Dictionary<string, string[]> Validate(GemReferenceContent content, DateOnly today,
        IReadOnlyDictionary<Guid, Guid?> redirects)
    {
        var errors = ValidateFieldsAndSources(content, today, requireSources: true);
        void Reject() => errors["retirement"] = ["Review this reference field and its supporting sources."];
        if (!ValidText(content.RetirementExplanation, 2000, false)) Reject();
        if (!content.IsRetired && (content.RedirectEntryId is not null || content.RetirementExplanation is not null)) Reject();
        if (content.IsRetired && content.RedirectEntryId is null && string.IsNullOrWhiteSpace(content.RetirementExplanation)) Reject();
        var seen = new HashSet<Guid> { content.Id };
        var target = content.RedirectEntryId;
        while (target is { } id)
        {
            if (!seen.Add(id) || !redirects.TryGetValue(id, out target))
            {
                Reject();
                break;
            }
        }
        return errors;
    }

    internal static Dictionary<string, string[]> ValidateFieldsAndSources(GemReferenceContent content,
        DateOnly today, bool requireSources)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Reject(string field) => errors[field] = ["Review this reference field and its supporting sources."];
        void Check(string field, string? text, int limit, bool required = false)
        {
            if ((required && string.IsNullOrWhiteSpace(text)) || text?.Length > limit ||
                text?.Any(char.IsControl) == true || (text is not null && string.IsNullOrWhiteSpace(text))) Reject(field);
        }
        if (content.Id == Guid.Empty) Reject("id");
        if (!MaterialKinds.Contains(content.MaterialKind)) Reject("materialKind");
        Check("commonName", content.CommonName, 200, true);
        Check("group", content.Group, 200);
        Check("species", content.Species, 200, content.MaterialKind == "mineral");
        Check("variety", content.Variety, 200);
        Check("description", content.Description, 2000);
        if (content.Aliases.Count > 20 || content.Aliases.Any(value => string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 || value.Any(char.IsControl)) ||
            content.Aliases.Select(Comparison).Distinct(StringComparer.Ordinal).Count() != content.Aliases.Count) Reject("aliases");

        var populated = new HashSet<string>(StringComparer.Ordinal) { "materialKind", "commonName" };
        if (content.Group is not null) populated.Add("group");
        if (content.Species is not null) populated.Add("species");
        if (content.Variety is not null) populated.Add("variety");
        if (content.Description is not null) populated.Add("description");
        if (content.Aliases.Count != 0) populated.Add("aliases");
        if (content.NotableLocality is not null) populated.Add("notableLocality");
        if (content.Sources.Count > 64 || content.Sources.Select(source => source.Id).Distinct().Count() != content.Sources.Count)
            Reject("sources");
        foreach (var source in content.Sources)
        {
            if (source.Id == Guid.Empty || !Fields.Contains(source.Field) || !populated.Contains(source.Field) ||
                !ValidText(source.Title, 200, true) || !ValidText(source.Publisher, 200, true) ||
                !ValidText(source.Citation, 2000, false) || !ValidText(source.Url, 2000, false) ||
                (source.Url is null && source.Citation is null) ||
                (source.Url is not null && !SafeUrl(source.Url)) ||
                source.ReviewedOn == default || source.ReviewedOn > today ||
                source.AccessedOn == DateOnly.MinValue || source.AccessedOn > today) Reject("sources");
        }
        if (requireSources && populated.Except(content.Sources.Select(source => source.Field), StringComparer.Ordinal).Any()) Reject("sources");
        if (content.NotableLocality is { } locality)
        {
            var source = content.Sources.FirstOrDefault(source => source.Id == locality.SourceAssertionId);
            if (!ValidText(locality.Place, 200, true) || !ValidText(locality.Scope, 200, true) ||
                locality.ReviewedOn == default || locality.ReviewedOn > today ||
                source?.Field != "notableLocality" || source.ReviewedOn != locality.ReviewedOn) Reject("notableLocality");
        }
        return errors;
    }

    private static bool ValidText(string? value, int limit, bool required) =>
        value is null ? !required : !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);

    private static bool SafeUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    public static byte[] IdentityKey(GemReferenceContent content)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var component in new[] { content.MaterialKind, content.Group, content.Species, content.Variety, content.CommonName })
            {
                writer.Write(component is not null);
                if (component is not null) writer.Write(Comparison(component));
            }
        }
        return SHA256.HashData(bytes.ToArray());
    }
}
