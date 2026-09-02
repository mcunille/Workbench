// Copyright (c) 2026 The White Stag Collection.

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
        var first = new SqlSensitiveRequestRateLimiter(webConnection, TimeProvider.System);
        var second = new SqlSensitiveRequestRateLimiter(webConnection, TimeProvider.System);

        for (var request = 0; request < 5; request++)
        {
            Assert.True(await (request % 2 == 0 ? first : second)
                .TryAcquireAsync("shared-login-window", CancellationToken.None));
        }

        Assert.False(await second.TryAcquireAsync("shared-login-window", CancellationToken.None));
    }
}
