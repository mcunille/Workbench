// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;

namespace Workbench.Server.Gemology;

public static class GemReferenceReview
{
    public static GemReferenceEntryReview Build(GemReferenceDraftResponse draft, GemReferenceContent? published,
        string? publishedVersion, IReadOnlyList<GemReferenceContent> finalCatalog, DateOnly today)
    {
        var normalized = GemReferenceInput.Normalize(draft.Content);
        var before = published is null ? default : JsonSerializer.SerializeToElement(published, GemReferenceCurationSql.Json);
        var after = JsonSerializer.SerializeToElement(normalized, GemReferenceCurationSql.Json);
        var changes = new List<GemReferenceFieldChange>();
        foreach (var field in after.EnumerateObject().Where(p => p.Name != "id"))
        {
            var old = before.ValueKind == JsonValueKind.Object && before.TryGetProperty(field.Name, out var value) ? value : default;
            if (old.ValueKind == JsonValueKind.Undefined || old.GetRawText() != field.Value.GetRawText())
                changes.Add(new(field.Name, Display(old), Display(field.Value)));
        }
        return new(draft.Id, draft.EntryId, draft.ExpectedPublishedRowVersion != publishedVersion, changes, Errors(normalized, finalCatalog, today));
    }

    internal static Dictionary<string, string[]> Errors(GemReferenceContent content, IReadOnlyList<GemReferenceContent> finalCatalog, DateOnly today)
    {
        content = GemReferenceInput.Normalize(content);
        var redirects = finalCatalog.ToDictionary(e => e.Id, e => e.RedirectEntryId);
        var errors = GemReferenceInput.Validate(content, today, redirects);
        if (!content.IsRetired && finalCatalog.Any(e => e.Id != content.Id && !e.IsRetired &&
            GemReferenceInput.IdentityKey(GemReferenceInput.Normalize(e)).SequenceEqual(GemReferenceInput.IdentityKey(content))))
            errors["identity"] = ["Another active shared entry has this identity."];
        if (finalCatalog.Any(e => e.Id != content.Id && e.Sources.Any(s => content.Sources.Any(own => own.Id == s.Id))))
            errors["sources"] = ["A source assertion ID belongs to another entry."];
        return errors;
    }

    private static object? Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => value.Clone()
    };
}
