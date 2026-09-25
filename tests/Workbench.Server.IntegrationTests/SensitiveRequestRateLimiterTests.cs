// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using Workbench.Server.Tenancy;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SensitiveRequestRateLimiterTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task SqlLimiterSharesAWindowAcrossApplicationReplicas()
    {
        // GIVEN an isolated current schema for the replicas' shared durable rate limit.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var webConnection = await database.CreateWebUserAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        var first = new SqlSensitiveRequestRateLimiter(webConnection, proof);
        var second = new SqlSensitiveRequestRateLimiter(webConnection, proof);

        // WHEN replicas alternate requests THEN they consume the same five permits.
        for (var request = 0; request < 5; request++)
        {
            Assert.True(await (request % 2 == 0 ? first : second)
                .TryAcquireAsync("shared-login-window", CancellationToken.None));
        }

        // AND the next request is rejected by the shared persisted limit.
        Assert.False(await second.TryAcquireAsync("shared-login-window", CancellationToken.None));
        // THEN the stored partition cannot be reproduced with an unkeyed dictionary hash.
        await using var inspection = new SqlConnection(database.AdminConnectionString);
        await inspection.OpenAsync();
        await using var readPartition = new SqlCommand("SELECT TOP (1) [PartitionHash] FROM [Security].[SensitiveRequestLimits]", inspection);
        Assert.NotEqual(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("shared-login-window")),
            (byte[])(await readPartition.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task WebPrincipalCannotOverrideLimiterTimeWindowOrPermitCount()
    {
        // GIVEN an isolated current schema and the same restricted web authority as production.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var webConnection = await database.CreateWebUserAsync();
        await using var connection = new SqlConnection(webConnection);
        await connection.OpenAsync();
        await using var command = new SqlCommand("[Security].[TryAcquireSensitiveRequest]", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.Add(new SqlParameter("@PartitionHash", SqlDbType.Binary, 32)
        {
            Value = SHA256.HashData("attacker-controlled"u8),
        });
        command.Parameters.AddWithValue("@Now", DateTimeOffset.MaxValue.AddDays(-1));
        command.Parameters.AddWithValue("@WindowSeconds", int.MaxValue);
        command.Parameters.AddWithValue("@PermitLimit", int.MaxValue);

        // WHEN the web principal supplies its own quota parameters THEN SQL rejects the call.
        await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task SqlLimiterOpportunisticallyRemovesExpiredPartitions()
    {
        // GIVEN an isolated current schema in which only this test seeds expired partitions.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var webConnection = await database.CreateWebUserAsync();
        await using (var connection = new SqlConnection(database.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new SqlCommand("""
                INSERT INTO [Security].[SensitiveRequestLimits]
                    ([PartitionHash], [WindowStartedAtUtc], [RequestCount])
                VALUES
                    (@first, '2000-01-01T00:00:00+00:00', 1),
                    (@second, '2000-01-01T00:00:00+00:00', 1);
                """, connection);
            seed.Parameters.AddWithValue("@first", SHA256.HashData("expired-first"u8));
            seed.Parameters.AddWithValue("@second", SHA256.HashData("expired-second"u8));
            await seed.ExecuteNonQueryAsync();
        }

        // WHEN the web principal acquires a current permit THEN it succeeds.
        Assert.True(await new SqlSensitiveRequestRateLimiter(webConnection, new TenantContextProof(await database.GetTenantContextProofKeyAsync()))
            .TryAcquireAsync("current-partition", CancellationToken.None));

        await using var verifyConnection = new SqlConnection(database.AdminConnectionString);
        await verifyConnection.OpenAsync();
        await using var verify = new SqlCommand(
            "SELECT COUNT(*) FROM [Security].[SensitiveRequestLimits] WHERE [WindowStartedAtUtc] < '2001-01-01'",
            verifyConnection);
        // AND the expired partitions have been removed from SQL.
        Assert.Equal(0, Convert.ToInt32(await verify.ExecuteScalarAsync()));
    }
}
