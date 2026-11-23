// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Persistence.Migrations;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferencePilotAcceptanceTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshAndUpgradedPilotMatchReviewedPackageAndReplayPreservesPublishedEdits(bool upgrade)
    {
        // GIVEN an empty database or the actual merged pre-pilot baseline and the separately maintained reviewed package.
        await using var database = await sqlServer.CreateDatabaseAsync();
        if (upgrade) await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddServiceAdminIdentity", default);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Workbench.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var sample = JsonSerializer.Deserialize<GemReferenceContent[]>(
            await File.ReadAllTextAsync(Path.Combine(root.FullName, "data", "gem-reference", "sample.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(4, sample.Length);
        Assert.Equal(19, sample.Sum(entry => entry.Sources.Count));

        // WHEN installing current migrations THEN all distributed identities, fields and claim-level source bytes match.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        var catalog = new GemReferenceAdminReadService(database.AdminConnectionString);
        Assert.Equal(4, (await catalog.BrowseAsync(null, null, null, null, default)).Entries.Count);
        foreach (var expected in sample)
        {
            var actual = Assert.IsType<GemReferenceDetailResponse>(await catalog.DetailAsync(expected.Id, default));
            var actualContent = new GemReferenceContent(actual.Id, actual.MaterialKind, actual.CommonName, actual.Group,
                actual.Species, actual.Variety, actual.Description, actual.Aliases,
                actual.SourceAssertions.Select(source => new GemReferenceSourceContent(source.Id, source.Field, source.Title,
                    source.Publisher, source.Url, source.Citation, source.AccessedOn, source.ReviewedOn)).OrderBy(source => source.Id).ToArray(),
                actual.NotableLocality, actual.Retirement.IsRetired, actual.Retirement.Explanation, actual.Retirement.RedirectEntryId);
            Assert.Equal(JsonSerializer.Serialize(expected with { Sources = expected.Sources.OrderBy(source => source.Id).ToArray() }),
                JsonSerializer.Serialize(actualContent));
        }

        // AND real service-admin publication corrects one pilot and retires another before installer replay.
        await using var application = await AuthTestApplication.CreateFromDatabaseAsync(database);
        await application.ProvisionServiceAdminAsync();
        var session = await GemReferenceDraftTests.CreateSessionAsync(application);
        var ruby = sample.Single(entry => entry.CommonName == "Ruby");
        var diamond = sample.Single(entry => entry.CommonName == "Diamond");
        var corrected = ruby with
        {
            Description = "Synthetic published correction retained across installer replay.",
            Sources = [.. ruby.Sources.Where(source => source.Field != "description"), GemReferenceSamples.Source("description")],
        };
        var retired = diamond with { IsRetired = true, RetirementExplanation = "Synthetic acceptance retirement." };
        var drafts = new GemReferenceDraftService(application.WebConnectionString);
        var selections = new List<GemReferenceDraftSelection>();
        foreach (var content in new[] { corrected, retired })
        {
            var current = (await catalog.DetailAsync(content.Id, default))!;
            var draft = await drafts.SaveAsync(AuthTestApplication.ServiceAdminId, session, Guid.NewGuid(),
                new(content.Id, content, null, current.RowVersion), default);
            selections.Add(new(draft.Id, draft.RowVersion));
        }
        Assert.Equal("published", (await new GemReferencePublicationService(application.WebConnectionString).PublishAsync(
            AuthTestApplication.ServiceAdminId, session, new(Guid.NewGuid(), selections), default)).Code);
        var before = await GemReferenceRecoveryEvidence.SnapshotAsync(database.AdminConnectionString);

        // WHEN the frozen installer is replayed and migrations rerun THEN no values, versions, assertions or audit evidence change.
        using var stream = typeof(InstallGemReferencePilot).Assembly.GetManifestResourceStream("Workbench.GemReferencePilot.sql")!;
        using var reader = new StreamReader(stream);
        await ServiceAdminRestoreTests.ExecuteAsync(database.AdminConnectionString, await reader.ReadToEndAsync());
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await GemReferenceRecoveryEvidence.SnapshotAsync(database.AdminConnectionString));
        Assert.Equal(corrected.Description, (await catalog.DetailAsync(ruby.Id, default))!.Description);
        Assert.True((await catalog.DetailAsync(diamond.Id, default))!.Retirement.IsRetired);
    }
}
