// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalMigrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task UpgradeFromAccountingFoundationPreservesConfigurationAccountsAndReplayBytes()
    {
        // GIVEN merged BK-01 with a configured tenant, chart and successful immutable request receipts.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: "AddAccountingFoundation");
        await using var admin = new SqlConnection(application.AdminConnectionString);
        await admin.OpenAsync();
        var session = Guid.NewGuid();
        await using var seed = new SqlCommand("""
            INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
              SELECT @tenant,@actor,RoleId FROM Administration.AccountingRoles WHERE TenantId=@tenant AND Kind='Administrator';
            INSERT [Identity].[Sessions](Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
              SELECT @session,@tenant,@actor,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME()) FROM [Identity].[Users] WHERE Id=@actor;
            SELECT ProofKey FROM Security.TenantContextKeys WHERE Id=1;
            """, admin);
        seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
        seed.Parameters.AddWithValue("@actor", AuthTestApplication.AdminUserId);
        seed.Parameters.AddWithValue("@session", session);
        var proof = (byte[])(await seed.ExecuteScalarAsync())!;
        await using var web = new SqlConnection(application.WebConnectionString);
        await web.OpenAsync();
        await new TenantContextProof(proof).ApplyAsync(web, AuthTestApplication.TenantId, default);
        var request = Guid.NewGuid();
        const string chart = """[{"code":"1000","name":"Retained asset","type":"Asset","purpose":"General"}]""";
        var original = await Save(request, "CreateAccounts", chart, null);
        var configurationRequest = Guid.NewGuid();
        const string configuration = """{"policies":{"currency":"USD","scale":2,"fiscalStartMonth":1,"startApproach":"FromBeginning","plannedStartDate":"2026-01-01"},"mappings":[],"coverage":[]}""";
        var configured = await Save(configurationRequest, "Configure", configuration, Guid.Empty);
        var before = await Snapshot();

        // WHEN the one BK-02 release migration upgrades the merged base.
        await DatabaseMigrator.MigrateAsync(application.AdminConnectionString, default);

        // THEN existing rows and canonical receipt payloads remain byte-identical and still replay.
        Assert.Equal(before, await Snapshot());
        Assert.Equal(original, await Save(request, "CreateAccounts", chart, null));
        Assert.Equal(configured, await Save(configurationRequest, "Configure", configuration, Guid.Empty));
        await MigrationHistoryAssertions.AssertCurrentAsync(application.AdminConnectionString);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId>N'20260921051843_AddAccountingFoundation'", admin);
        Assert.Equal(1, await count.ExecuteScalarAsync());
        // AND migration creates no invented financial entries or policy freeze.
        foreach (var table in new[] { "SourceEvents", "JournalEntries", "JournalLines", "PostingReceipts", "PolicyFreezes" })
        {
            count.CommandText = $"SELECT COUNT(*) FROM Accounting.{table}";
            Assert.Equal(0, await count.ExecuteScalarAsync());
        }
        // AND historical configuration still edits normally before any posting.
        var next = await Save(Guid.NewGuid(), "Configure", configuration.Replace("USD", "CAD", StringComparison.Ordinal), configured.Version);
        Assert.NotEqual(configured.Version, next.Version);
        Assert.Single(JsonSerializer.Deserialize<Guid[]>(original.AccountIds)!);

        async Task<(Guid Version, string AccountIds)> Save(Guid requestId, string operation, string payload, Guid? version)
        {
            await using var command = new SqlCommand("EXEC [Accounting].[Save] @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Operation=@operation,@ExpectedVersion=@version,@Payload=@payload", web);
            command.Parameters.AddWithValue("@actor", AuthTestApplication.AdminUserId);
            command.Parameters.AddWithValue("@session", session);
            command.Parameters.AddWithValue("@request", requestId);
            command.Parameters.AddWithValue("@operation", operation);
            command.Parameters.AddWithValue("@version", (object?)version ?? DBNull.Value);
            command.Parameters.AddWithValue("@payload", payload);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetGuid(0), reader.GetString(1));
        }

        async Task<string> Snapshot()
        {
            await using var command = new SqlCommand("""
                SELECT
                  (SELECT * FROM Accounting.Accounts ORDER BY Id FOR JSON PATH) Accounts,
                  (SELECT * FROM Accounting.Configurations ORDER BY TenantId FOR JSON PATH) Configurations,
                  (SELECT * FROM Accounting.Revisions ORDER BY Id FOR JSON PATH) Revisions,
                  (SELECT * FROM Accounting.Receipts ORDER BY RequestId FOR JSON PATH) Receipts,
                  (SELECT * FROM Administration.AccountingRoles ORDER BY RoleId FOR JSON PATH) Roles
                FOR JSON PATH;
                """, web);
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }
}
