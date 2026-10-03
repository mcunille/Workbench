// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;

namespace Workbench.Server.Gemology;

public static class GemReferenceTenantInput
{
    private static readonly IReadOnlySet<string> ClearableFields = new HashSet<string>(StringComparer.Ordinal)
        { "group", "species", "variety", "description", "notableLocality" };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Dictionary<string, string[]> Validate(GemReferenceContent content, DateOnly today)
    {
        var errors = GemReferenceInput.ValidateFieldsAndSources(SafeCollections(content), today, requireSources: false);
        if (content.Aliases is null) errors["aliases"] = ["Review this reference field and its supporting sources."];
        if (content.Sources is null || content.Sources.Any(source => source is null))
            errors["sources"] = ["Review this reference field and its supporting sources."];
        if (content.IsRetired || content.RedirectEntryId is not null || content.RetirementExplanation is not null)
            errors["retirement"] = ["Tenant entries cannot retire or redirect Workbench references."];
        return errors;
    }

    internal static GemReferenceContent SafeCollections(GemReferenceContent content) => content with
    {
        Aliases = content.Aliases?.Select(alias => alias ?? "").ToArray() ?? [],
        Sources = content.Sources?.Where(source => source is not null).ToArray() ?? [],
    };

    public static Dictionary<string, string[]> ValidateOverrides(
        IReadOnlyDictionary<string, GemReferenceFieldOverride> overrides, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var sourceIds = new HashSet<Guid>();
        var sourceCount = overrides.Values.Sum(choice => choice?.Sources?.Count ?? 0);
        foreach (var (field, choice) in overrides)
        {
            void Reject() => errors[field] = ["Review this field choice and its supporting sources."];
            if (!GemReferenceInput.Fields.Contains(field) || choice is null || choice.Sources is null ||
                choice.State is not ("inherit" or "replace" or "clear"))
            {
                Reject();
                continue;
            }
            if (choice.State != "replace")
            {
                if (choice.Sources.Count != 0 || !EmptyValue(choice.Value) ||
                    (choice.State == "clear" && !ClearableFields.Contains(field))) Reject();
                continue;
            }
            if (EmptyValue(choice.Value) || sourceCount > 64 ||
                choice.Sources.Any(source => source is null || source.Field != field || !sourceIds.Add(source.Id))) Reject();
            var baseline = new GemReferenceContent(Guid.NewGuid(), "mineral", "Reference", null,
                "Species", null, null, [], choice.Sources, null, false, null, null);
            if (!TryApply(baseline, field, choice, out var candidate) ||
                Validate(candidate, today).Count != 0) Reject();
        }
        return errors;
    }

    private static bool EmptyValue(JsonElement? value) => value is null ||
        value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    internal static bool TryApply(GemReferenceContent content, string field, GemReferenceFieldOverride choice,
        out GemReferenceContent result)
    {
        result = content;
        if (choice.State == "inherit") return true;
        if (choice.State is not ("replace" or "clear")) return false;
        try
        {
            var clear = choice.State == "clear";
            if (!clear && EmptyValue(choice.Value)) return false;
            string? Text() => clear ? null : choice.Value!.Value.GetString();
            result = field switch
            {
                "materialKind" when !clear => content with { MaterialKind = Text()! },
                "commonName" when !clear => content with { CommonName = Text()! },
                "group" => content with { Group = Text() },
                "species" => content with { Species = Text() },
                "variety" => content with { Variety = Text() },
                "description" => content with { Description = Text() },
                "aliases" when !clear => content with { Aliases = choice.Value!.Value.Deserialize<string[]>(JsonOptions)! },
                "notableLocality" => content with { NotableLocality = clear ? null : choice.Value!.Value.Deserialize<GemReferenceLocalityContent>(JsonOptions) },
                _ => content,
            };
            return GemReferenceInput.Fields.Contains(field) && (!clear || ClearableFields.Contains(field));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
