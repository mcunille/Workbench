// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Gemology;
using Workbench.Server.Persistence;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class GemReferenceTestData
{
    internal static async Task InsertAsync(string ownerConnection, GemReferenceContent content)
    {
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(ownerConnection).Options;
        await using var database = new WorkbenchDbContext(options);
        database.GemReferenceEntries.Add(new()
        {
            Id = content.Id,
            MaterialKind = content.MaterialKind,
            CommonName = content.CommonName,
            Group = content.Group,
            Species = content.Species,
            Variety = content.Variety,
            Description = content.Description,
            IdentityKey = GemReferenceInput.IdentityKey(content),
            IsRetired = content.IsRetired,
            RetirementExplanation = content.RetirementExplanation,
            RedirectEntryId = content.RedirectEntryId,
            Aliases = content.Aliases.Select((name, position) => new GemReferenceAlias
            { EntryId = content.Id, Position = position, Name = name, NormalizedName = GemReferenceInput.Comparison(name) }).ToList(),
            SourceAssertions = content.Sources.Select(source => new GemReferenceSourceAssertion
            {
                Id = source.Id,
                EntryId = content.Id,
                Field = source.Field,
                Title = source.Title,
                Publisher = source.Publisher,
                Url = source.Url,
                Citation = source.Citation,
                AccessedOn = source.AccessedOn,
                ReviewedOn = source.ReviewedOn,
            }).ToList(),
            NotableLocality = content.NotableLocality is { } locality ? new()
            {
                EntryId = content.Id,
                Place = locality.Place,
                Scope = locality.Scope,
                ReviewedOn = locality.ReviewedOn,
                SourceAssertionId = locality.SourceAssertionId,
            } : null,
        });
        await database.SaveChangesAsync();
    }
}
