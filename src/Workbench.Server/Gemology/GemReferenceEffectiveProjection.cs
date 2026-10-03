// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Gemology;

public static class GemReferenceEffectiveProjection
{
    public static GemReferenceDetailResponse Resolve(GemReferenceContent shared, string sharedRowVersion,
        IReadOnlyDictionary<string, GemReferenceFieldOverride> overrides, string? tenantRowVersion, DateOnly today)
    {
        var content = shared;
        var fields = new Dictionary<string, GemReferenceEffectiveField>(StringComparer.Ordinal);
        var errors = GemReferenceTenantInput.ValidateOverrides(overrides, today);
        var retained = overrides.Where(pair => pair.Value?.State != "inherit" || errors.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var field in GemReferenceInput.Fields)
        {
            var state = "inherit";
            var attribution = "workbench";
            IReadOnlyList<GemReferenceSourceContent> sources = shared.Sources.Where(source => source.Field == field).ToArray();
            if (retained.TryGetValue(field, out var choice))
            {
                state = "invalid";
                if (choice is not null && choice.State != "inherit" &&
                    GemReferenceTenantInput.TryApply(content, field, choice, out var applied))
                {
                    content = applied;
                    state = choice.State;
                    attribution = "tenant";
                    sources = choice.State == "replace" ? choice.Sources?.Where(source => source is not null && source.Field == field).ToArray() ?? [] : [];
                }
            }
            fields[field] = new(state, attribution, sources.Select(source => Source(source, attribution)).ToArray());
        }
        content = GemReferenceInput.Normalize(GemReferenceTenantInput.SafeCollections(content with
        {
            Sources = fields.Values.SelectMany(field => field.Sources)
            .Select(source => new GemReferenceSourceContent(source.Id, source.Field, source.Title, source.Publisher,
                source.Url, source.Citation, source.AccessedOn, source.ReviewedOn)).ToArray()
        }));
        foreach (var error in GemReferenceInput.ValidateFieldsAndSources(content, today, requireSources: false))
            errors[error.Key] = error.Value;
        return Detail(content, sharedRowVersion, retained.Count == 0 ? "workbenchReference" : "workbenchReferenceCustomized", fields, errors) with
        {
            WorkbenchContent = shared,
            Overrides = retained,
            EffectiveVersion = new(sharedRowVersion, tenantRowVersion),
        };
    }

    public static GemReferenceDetailResponse ResolveTenant(GemReferenceContent content, string tenantRowVersion,
        bool isArchived, DateOnly today)
    {
        var errors = GemReferenceTenantInput.Validate(content, today);
        content = GemReferenceInput.Normalize(GemReferenceTenantInput.SafeCollections(content));
        var fields = GemReferenceInput.Fields.ToDictionary(field => field,
            field => new GemReferenceEffectiveField("replace", "tenant", content.Sources.Where(source => source.Field == field)
                .Select(source => Source(source, "tenant")).ToArray()), StringComparer.Ordinal);
        return Detail(content, tenantRowVersion, "tenantEntry", fields, errors) with
        {
            EffectiveVersion = new(null, tenantRowVersion),
            IsArchived = isArchived,
        };
    }

    private static GemReferenceSourceResponse Source(GemReferenceSourceContent source, string attribution) =>
        new(source.Id, source.Field, source.Title, source.Publisher, source.Url, source.Citation,
            source.AccessedOn, source.ReviewedOn, attribution);

    private static GemReferenceDetailResponse Detail(GemReferenceContent content, string rowVersion, string layer,
        IReadOnlyDictionary<string, GemReferenceEffectiveField> fields, IReadOnlyDictionary<string, string[]> errors) =>
        new(content.Id, content.MaterialKind, content.CommonName, content.Group, content.Species, content.Variety,
            layer, content.Aliases, content.Description, rowVersion, fields.Values.SelectMany(field => field.Sources).ToArray(),
            content.NotableLocality, new(content.IsRetired, content.RetirementExplanation, content.RedirectEntryId))
        {
            EffectiveFields = fields,
            NeedsReview = errors.Count != 0,
            ReviewReasons = errors,
        };
}
