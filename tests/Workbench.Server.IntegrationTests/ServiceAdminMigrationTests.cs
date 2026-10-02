// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceAdminMigrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task MergedBaseUpgradePreservesTenantDataAndInstallsAdminSanitation()
    {
        // GIVEN the actual merged PR base with existing tenant identities and catalog provenance.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: "AddSharedGemReference");
        await GemReferenceTestData.InsertAsync(application.AdminConnectionString, GemReferenceSamples.Mineral());
        var catalogBefore = await ServiceAdminRestoreTests.ScalarAsync<string>(application.AdminConnectionString,
            "SELECT (SELECT * FROM Gemology.Entries ORDER BY Id FOR JSON PATH) AS Entries, (SELECT * FROM Gemology.SourceAssertions ORDER BY Id FOR JSON PATH) AS Sources FOR JSON PATH");
        // WHEN the coherent GEM-03 migration upgrades that database.
        await DatabaseMigrator.MigrateAsync(application.AdminConnectionString, CancellationToken.None);
        await application.ProvisionServiceAdminAsync();
        using var client = application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await ServiceAdminRestoreTests.LoginAsync(client)).StatusCode);
        var before = await ServiceAdminRestoreTests.ScalarAsync<long>(application.AdminConnectionString, "SELECT SecurityVersion FROM ServiceAdministration.Accounts");
        await ServiceAdminRestoreTests.ExecuteAsync(application.AdminConnectionString,
            "DECLARE @now datetimeoffset=SYSDATETIMEOFFSET(); EXEC Administration.SanitizeRestore @Now=@now,@CorrelationId=N'gem03-upgrade'");
        // THEN tenant rows, catalog bytes including rowversion/provenance, and history survive.
        // AND the upgraded sanitation boundary removes admin authority.
        Assert.Equal(catalogBefore, await ServiceAdminRestoreTests.ScalarAsync<string>(application.AdminConnectionString,
            "SELECT (SELECT * FROM Gemology.Entries ORDER BY Id FOR JSON PATH) AS Entries, (SELECT * FROM Gemology.SourceAssertions ORDER BY Id FOR JSON PATH) AS Sources FOR JSON PATH"));
        Assert.Equal(4, await ServiceAdminRestoreTests.ScalarAsync<int>(application.AdminConnectionString, "SELECT COUNT(*) FROM [Identity].Users"));
        Assert.Equal(0, await ServiceAdminRestoreTests.ScalarAsync<int>(application.AdminConnectionString, "SELECT COUNT(*) FROM ServiceAdministration.Sessions"));
        Assert.Equal(before + 1, await ServiceAdminRestoreTests.ScalarAsync<long>(application.AdminConnectionString, "SELECT SecurityVersion FROM ServiceAdministration.Accounts"));
        await MigrationHistoryAssertions.AssertCurrentAsync(application.AdminConnectionString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task UnsupportedSanitationPredecessorRefusesUpgradeWithoutPartialSchema()
    {
        // GIVEN an unexpected sanitation body at the merged base.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("AddSharedGemReference");
        await ServiceAdminRestoreTests.ExecuteAsync(database.AdminConnectionString, "ALTER PROCEDURE Administration.SanitizeRestore AS SELECT 1;");
        // WHEN the release migration attempts to install its security extension.
        var error = await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None));
        // THEN unsupported state fails closed and the migration transaction leaves no admin table or history.
        Assert.Equal(50020, error.Number);
        Assert.Equal(0, await ServiceAdminRestoreTests.ScalarAsync<int>(database.AdminConnectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'ServiceAdministration.Accounts')"));
        Assert.Equal("20261001000000_AddSharedGemReference", await ServiceAdminRestoreTests.ScalarAsync<string>(database.AdminConnectionString,
            "SELECT MAX(MigrationId) FROM dbo.__EFMigrationsHistory"));
    }
}
