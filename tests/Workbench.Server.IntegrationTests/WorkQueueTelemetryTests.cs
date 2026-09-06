// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Operations;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class WorkQueueTelemetryTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task EmptyQueueReturnsZeroCounts()
    {
        // GIVEN an empty migrated database and a dedicated worker principal.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var worker = await database.CreateRoleUserAsync("workbench_worker");
        // WHEN the worker reads aggregate queue status.
        var result = await WorkQueueTelemetry.ReadAsync(worker, CancellationToken.None);
        // THEN no pending work or age is reported.
        Assert.Equal(new WorkQueueStatus(0, 0), result);
    }

    [Fact]
    public async Task WorkerSeesOnlyOneAggregateRowAcrossTenantsAndPendingStates()
    {
        // GIVEN ready and leased work across two tenants, plus older terminal work.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(first, second);
        await SeedAsync(database.AdminConnectionString, first, WorkState.Ready, -600);
        await SeedAsync(database.AdminConnectionString, second, WorkState.Leased, -300);
        await SeedAsync(database.AdminConnectionString, first, WorkState.Completed, -3600);
        await SeedAsync(database.AdminConnectionString, second, WorkState.Dead, -7200);
        var worker = await database.CreateRoleUserAsync("workbench_worker");
        // WHEN a worker without any tenant context reads the status.
        var result = await WorkQueueTelemetry.ReadAsync(worker, CancellationToken.None);
        // THEN only pending states contribute and the oldest due age is reported.
        Assert.Equal(2, result.PendingCount);
        Assert.InRange(result.OldestPendingAgeSeconds, 600, 630);
        await using var connection = new SqlConnection(worker);
        await connection.OpenAsync();
        await using var command = new SqlCommand("[Operations].[ReadWorkQueueStatus]", connection) { CommandType = CommandType.StoredProcedure };
        await using (var reader = await command.ExecuteReaderAsync())
        {
            // AND its entire result contains only the two aggregate fields, with no work or tenant identifiers.
            Assert.Equal(2, reader.FieldCount);
            Assert.Equal("PendingCount", reader.GetName(0));
            Assert.Equal("OldestPendingAgeSeconds", reader.GetName(1));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
        }
        // AND the elevated aggregate procedure does not grant cross-tenant row visibility.
        await using var rows = new SqlCommand("SELECT COUNT(*) FROM [Operations].[WorkItems]", connection);
        Assert.Equal(0, Convert.ToInt32(await rows.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task FutureScheduledWorkDoesNotReportNegativeOrPrematureAge()
    {
        // GIVEN ready work whose retention delay has not elapsed.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        await SeedAsync(database.AdminConnectionString, tenant, WorkState.Ready, 3600);
        var worker = await database.CreateRoleUserAsync("workbench_worker");
        // WHEN queue status is read before the work becomes due.
        var result = await WorkQueueTelemetry.ReadAsync(worker, CancellationToken.None);
        // THEN pending work is counted but it has no elapsed due age.
        Assert.Equal(new WorkQueueStatus(1, 0), result);
    }

    [Fact]
    public async Task WebPrincipalCannotReadCrossTenantAggregate()
    {
        // GIVEN a runtime web principal with no worker membership.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var web = await database.CreateWebUserAsync();
        // WHEN the web principal attempts the aggregate procedure.
        var error = await Assert.ThrowsAsync<SqlException>(() => WorkQueueTelemetry.ReadAsync(web, CancellationToken.None));
        // THEN SQL denies execution rather than disclosing queue metadata.
        Assert.Equal(229, error.Number);
    }

    [Fact]
    public async Task TelemetryMigrationCanBeRolledBackAndReappliedWithoutRemovingWork()
    {
        // GIVEN queued work under the telemetry release schema.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        await SeedAsync(database.AdminConnectionString, tenant, WorkState.Ready, 3600);
        var worker = await database.CreateRoleUserAsync("workbench_worker");
        // WHEN only the additive telemetry migration is rolled back to the PR base.
        await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddBlobAndOperationalProviders", CancellationToken.None);
        var missing = await Assert.ThrowsAsync<SqlException>(() => WorkQueueTelemetry.ReadAsync(worker, CancellationToken.None));
        Assert.Equal(2812, missing.Number);
        // THEN reapplying restores the procedure and its grant while retaining the queued work.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        Assert.Equal(new WorkQueueStatus(1, 0), await WorkQueueTelemetry.ReadAsync(worker, CancellationToken.None));
    }

    private static async Task SeedAsync(string connectionString, Guid tenant, WorkState state, int availableSeconds)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT INTO [Storage].[Attachments] ([Id], [TenantId], [CreatedAtUtc])
                VALUES (@attachment, @tenant, DATEADD(day,-8,SYSUTCDATETIME()));
            INSERT INTO [Operations].[WorkItems]
                ([Id], [TenantId], [Kind], [AttachmentId], [CreatedAtUtc], [AvailableAtUtc], [State])
                VALUES (NEWID(), @tenant, 1, @attachment, DATEADD(day,-8,SYSUTCDATETIME()),
                    DATEADD(second,@available,SYSUTCDATETIME()), @state);
            """, connection);
        command.Parameters.AddWithValue("@attachment", Guid.NewGuid());
        command.Parameters.AddWithValue("@tenant", tenant);
        command.Parameters.AddWithValue("@state", (int)state);
        command.Parameters.AddWithValue("@available", availableSeconds);
        await command.ExecuteNonQueryAsync();
    }
}
