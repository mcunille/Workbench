// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SensitiveRequestRateLimiterTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task SqlLimiterSharesAWindowAcrossApplicationReplicas()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var webConnection = await database.CreateWebUserAsync();
        var first = new SqlSensitiveRequestRateLimiter(webConnection);
        var second = new SqlSensitiveRequestRateLimiter(webConnection);

        for (var request = 0; request < 5; request++)
        {
            Assert.True(await (request % 2 == 0 ? first : second)
                .TryAcquireAsync("shared-login-window", CancellationToken.None));
        }

        Assert.False(await second.TryAcquireAsync("shared-login-window", CancellationToken.None));
    }

    [Fact]
    public async Task WebPrincipalCannotOverrideLimiterTimeWindowOrPermitCount()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
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

        await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task SqlLimiterOpportunisticallyRemovesExpiredPartitions()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
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

        Assert.True(await new SqlSensitiveRequestRateLimiter(webConnection)
            .TryAcquireAsync("current-partition", CancellationToken.None));

        await using var verifyConnection = new SqlConnection(database.AdminConnectionString);
        await verifyConnection.OpenAsync();
        await using var verify = new SqlCommand(
            "SELECT COUNT(*) FROM [Security].[SensitiveRequestLimits] WHERE [WindowStartedAtUtc] < '2001-01-01'",
            verifyConnection);
        Assert.Equal(0, Convert.ToInt32(await verify.ExecuteScalarAsync()));
    }
}
