// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DurableWorkTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task IndependentWorkersCannotClaimTheSameLeaseAndExpiredOwnersCannotComplete()
    {
        // GIVEN one durable deletion job created in tenant scope.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        var web = await database.CreateWebUserAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        var job = Guid.NewGuid();
        await using (var context = BlobPersistenceTests.CreateContext(web, proof, tenant))
        {
            var attachment = Guid.NewGuid();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Storage].[Attachments] ([Id], [TenantId], [CreatedAtUtc])
                VALUES ({attachment}, {tenant}, {DateTimeOffset.UtcNow});
                INSERT INTO [Operations].[WorkItems]
                    ([Id], [TenantId], [Kind], [AttachmentId], [CreatedAtUtc], [AvailableAtUtc])
                VALUES ({job}, {tenant}, {1}, {attachment}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow});
                """);
        }
        var worker = await database.CreateRoleUserAsync("workbench_worker");
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        // WHEN independent connections compete for work.
        var claims = await Task.WhenAll(ClaimAsync(worker, firstOwner), ClaimAsync(worker, secondOwner));
        // THEN exactly one receives the lease.
        Assert.Single(claims, claim => claim is not null);
        var winner = claims[0] is not null ? firstOwner : secondOwner;
        var first = claims.Single(claim => claim is not null)!.Value;
        Assert.Equal(job, first.Id);
        // AND after lease expiry a different generation fences out the old owner.
        await using (var ownerConnection = new SqlConnection(database.AdminConnectionString))
        {
            await ownerConnection.OpenAsync();
            await using var expire = new SqlCommand("UPDATE [Operations].[WorkItems] SET [LeaseExpiresAtUtc] = DATEADD(second,-1,SYSUTCDATETIME())", ownerConnection);
            await expire.ExecuteNonQueryAsync();
        }
        var newer = await ClaimAsync(worker, Guid.NewGuid());
        Assert.NotNull(newer);
        Assert.True(newer.Value.Generation > first.Generation);
        await using var completionConnection = new SqlConnection(worker);
        await completionConnection.OpenAsync();
        await proof.ApplyAsync(completionConnection, tenant, CancellationToken.None);
        await using var completion = new SqlCommand("[Operations].[CompleteWork]", completionConnection) { CommandType = CommandType.StoredProcedure };
        completion.Parameters.AddWithValue("@Id", job);
        completion.Parameters.AddWithValue("@Owner", winner);
        completion.Parameters.AddWithValue("@Generation", first.Generation);
        Assert.Equal(0, Convert.ToInt32(await completion.ExecuteScalarAsync()));
    }

    private static async Task<(Guid Id, long Generation)?> ClaimAsync(string connectionString, Guid owner)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("[Operations].[ClaimWork]", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Owner", owner);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetGuid(0), reader.GetInt64(4)) : null;
    }
}
