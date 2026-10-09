// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferenceTenantMigrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task FreshSchemaContainsTenantStorageAndCurrentReadiness()
    {
        // GIVEN a genuinely empty database, independent of the schema template.
        await using var database = await sqlServer.CreateDatabaseAsync();
        // WHEN migrating current source THEN both tenant tables and the ordered readiness contract exist.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(2, await ServiceAdminIdentityDatabaseTests.ScalarAsync<int>(database.AdminConnectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'Gemology') AND name IN(N'TenantEntries',N'TenantOverrides')"));
        await MigrationHistoryAssertions.AssertCurrentAsync(database.AdminConnectionString);
    }

    [Fact]
    public async Task MergedCurationUpgradePreservesEvidenceAndGuardsDestructiveDown()
    {
        // GIVEN the merged GEM-05 baseline with shared provenance and tenant/admin identity evidence.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: "20261002192901_AddGemReferenceCuration");
        await app.ProvisionServiceAdminAsync();
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, GemReferenceSamples.Mineral());
        const string state = """
            SELECT (SELECT * FROM Gemology.Entries ORDER BY Id FOR JSON PATH) AS Entries,
                (SELECT * FROM Gemology.SourceAssertions ORDER BY Id FOR JSON PATH) AS Sources,
                (SELECT * FROM ServiceAdministration.Accounts ORDER BY Id FOR JSON PATH) AS Accounts,
                (SELECT Id,TenantId,SecurityVersion FROM [Identity].Users ORDER BY Id FOR JSON PATH) AS Users,
                (SELECT Id,Name,IsEnabled FROM Tenancy.Tenants ORDER BY Id FOR JSON PATH) AS Tenants FOR JSON PATH
            """;
        var before = await ServiceAdminIdentityDatabaseTests.ScalarAsync<string>(app.AdminConnectionString, state);
        // WHEN upgrading THEN existing rows remain byte-for-byte unchanged and history/readiness advance.
        await DatabaseMigrator.MigrateAsync(app.AdminConnectionString, default);
        Assert.Equal(before, await ServiceAdminIdentityDatabaseTests.ScalarAsync<string>(app.AdminConnectionString, state));
        await MigrationHistoryAssertions.AssertCurrentAsync(app.AdminConnectionString);
        // AND attempted downgrade refuses destructive tenant-storage loss before changing migration history.
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(app.AdminConnectionString,
            "20261002192901_AddGemReferenceCuration", default))).Number);
        Assert.Equal(before, await ServiceAdminIdentityDatabaseTests.ScalarAsync<string>(app.AdminConnectionString, state));
        await MigrationHistoryAssertions.AssertCurrentAsync(app.AdminConnectionString);
    }
}
