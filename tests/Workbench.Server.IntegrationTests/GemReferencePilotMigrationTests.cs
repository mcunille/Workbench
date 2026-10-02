// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferencePilotMigrationTests(SqlServerFixture sqlServer)
{
    private const string Baseline = "20261001072507_AddServiceAdminIdentity";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task FreshInstallationMatchesReviewedPackageAndRerunPreservesPublishedEdits()
    {
        // GIVEN a fresh database and the independently committed reviewed distribution package.
        await using var database = await sqlServer.CreateDatabaseAsync();
        var expected = await SampleAsync();
        Assert.Equal(4, expected.Length);
        Assert.Equal(19, expected.Sum(entry => entry.Sources.Count));
        // WHEN installing THEN all values, absent claims, stable IDs and citations match the package.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        await using var context = Context(database.AdminConnectionString);
        var installed = await EntriesAsync(context);
        Assert.Equal(Canonical(expected), Canonical(installed));
        Assert.All(installed, entry => Assert.Empty(GemReferenceInput.Validate(entry, new(2026, 10, 2), new Dictionary<Guid, Guid?>())));
        var rows = await context.GemReferenceEntries.AsNoTracking().ToArrayAsync();
        Assert.All(rows, row => Assert.Equal(GemReferenceInput.IdentityKey(expected.Single(entry => entry.Id == row.Id)), row.IdentityKey));
        // AND a later published value and citation are retained byte-for-byte on redeployment.
        await context.Database.ExecuteSqlRawAsync("""
            UPDATE Gemology.Entries SET Description=N'Published correction' WHERE CommonName=N'Diamond';
            UPDATE Gemology.SourceAssertions SET Title=N'Corrected reference',ReviewedOn='2026-10-03'
                WHERE EntryId='357a74d0-721e-4738-b9fa-14710e7b2385' AND Field=N'description';
            """);
        var before = await StateAsync(database.AdminConnectionString);
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await StateAsync(database.AdminConnectionString));
        await MigrationHistoryAssertions.AssertCurrentAsync(database.AdminConnectionString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergedBaseUpgradePreservesExistingPilotIdentityAndItsSources(bool retired)
    {
        // GIVEN the merged schema with an already curated pilot ID and different provenance.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync(Baseline);
        var sample = await SampleAsync();
        var original = sample[0] with
        {
            Description = "Previously published correction",
            Sources = [.. sample[0].Sources.Select(source => source with { Id = Guid.NewGuid(), Title = "Previously reviewed source" })],
            IsRetired = retired,
            RetirementExplanation = retired ? "Previously retired by curator" : null
        };
        await GemReferenceTestData.InsertAsync(database.AdminConnectionString, original);
        var before = await StateAsync(database.AdminConnectionString, original.Id);
        // WHEN upgrading THEN the retained entry, its sources and rowversion survive, and missing entries install.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await StateAsync(database.AdminConnectionString, original.Id));
        await using var context = Context(database.AdminConnectionString);
        Assert.Equal(Canonical([original, .. sample.Skip(1)]), Canonical(await EntriesAsync(context)));
        await MigrationHistoryAssertions.AssertCurrentAsync(database.AdminConnectionString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingPublishedContentRejectsEntireInstallationWithoutChangingMergedBase(bool sourceCollision)
    {
        // GIVEN a published identity or source ID conflict in the last pilot entry.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync(Baseline);
        var sample = await SampleAsync();
        var conflicting = sample[^1] with { Id = Guid.NewGuid() };
        if (sourceCollision) conflicting = conflicting with { CommonName = "Previously curated entry" };
        await GemReferenceTestData.InsertAsync(database.AdminConnectionString, conflicting);
        var before = await StateAsync(database.AdminConnectionString);
        // WHEN upgrading THEN conflict is explicit, existing data survives and no partial pilot or history commits.
        var failure = await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default));
        Assert.Equal(sourceCollision ? 2627 : 50020, failure.Number);
        Assert.Equal(before, await StateAsync(database.AdminConnectionString));
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT TOP(1) MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC", connection);
        Assert.Equal(Baseline, await command.ExecuteScalarAsync());
    }

    private static async Task<GemReferenceContent[]> SampleAsync() =>
        JsonSerializer.Deserialize<GemReferenceContent[]>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "gem-reference-sample.json")), Json)!;

    private static WorkbenchDbContext Context(string connection) => new(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(connection).Options);

    private static async Task<GemReferenceContent[]> EntriesAsync(WorkbenchDbContext context) =>
        (await context.GemReferenceEntries.AsNoTracking().Include(entry => entry.Aliases)
            .Include(entry => entry.SourceAssertions).Include(entry => entry.NotableLocality).ToArrayAsync())
        .Select(entry => new GemReferenceContent(entry.Id, entry.MaterialKind, entry.CommonName, entry.Group,
            entry.Species, entry.Variety, entry.Description, entry.Aliases.OrderBy(alias => alias.Position).Select(alias => alias.Name).ToArray(),
            entry.SourceAssertions.Select(source => new GemReferenceSourceContent(source.Id, source.Field, source.Title,
                source.Publisher, source.Url, source.Citation, source.AccessedOn, source.ReviewedOn)).ToArray(),
            entry.NotableLocality is { } locality ? new(locality.Place, locality.Scope, locality.ReviewedOn, locality.SourceAssertionId) : null,
            entry.IsRetired, entry.RetirementExplanation, entry.RedirectEntryId)).ToArray();

    private static string Canonical(IEnumerable<GemReferenceContent> entries) => JsonSerializer.Serialize(
        entries.OrderBy(entry => entry.Id).Select(entry => entry with { Sources = entry.Sources.OrderBy(source => source.Id).ToArray() }), Json);

    private static async Task<string> StateAsync(string connectionString, Guid? id = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT (SELECT * FROM Gemology.Entries WHERE @id IS NULL OR Id=@id ORDER BY Id FOR JSON PATH) AS Entries,
                (SELECT * FROM Gemology.SourceAssertions WHERE @id IS NULL OR EntryId=@id ORDER BY Id FOR JSON PATH) AS Sources,
                (SELECT * FROM Gemology.Aliases WHERE @id IS NULL OR EntryId=@id ORDER BY EntryId,Position FOR JSON PATH) AS Aliases,
                (SELECT * FROM Gemology.LocalityAssertions WHERE @id IS NULL OR EntryId=@id ORDER BY EntryId FOR JSON PATH) AS Localities
            FOR JSON PATH
            """, connection);
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = id is { } value ? value : DBNull.Value });
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
