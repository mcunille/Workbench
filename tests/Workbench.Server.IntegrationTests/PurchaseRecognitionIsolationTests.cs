// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionIsolationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PreparedInputsKeepFinancialStateAndAuthenticationIndependent()
    {
        // GIVEN two independently prepared current-schema recognition contexts.
        await using var first = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        await using var second = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        Assert.Equal(6, first.Accounts.Count);
        Assert.Equal(6, second.Accounts.Count);
        Assert.NotEqual(first.Journal.SessionId, second.Journal.SessionId);
        Assert.False(first.Journal.ProofKey.SequenceEqual(second.Journal.ProofKey));
        Assert.NotEqual(await ScalarAsync<string>(first, "SELECT SecurityStamp FROM [Identity].Users WHERE Id='11111111-1111-1111-1111-111111111111'"),
            await ScalarAsync<string>(second, "SELECT SecurityStamp FROM [Identity].Users WHERE Id='11111111-1111-1111-1111-111111111111'"));
        Assert.Equal(1, await ScalarAsync<int>(second, "SELECT COUNT(*) FROM [Identity].Sessions"));
        Assert.Equal(1, await ScalarAsync<int>(second, "SELECT COUNT(*) FROM sys.database_principals WHERE name LIKE 'workbench_web[_]%'"));

        // WHEN one context posts a real recognition and mutates its local account map.
        await first.PostAsync((await first.CommandAsync()).ToJsonString());
        first.Accounts.Clear();

        // THEN the other retains unposted inputs and can independently post under its own authority.
        Assert.Equal(6, second.Accounts.Count);
        Assert.Equal(0, await second.CountAsync("RecognitionUnits"));
        Assert.Equal(0m, await second.BalanceAsync("Inventory"));
        await second.PostAsync((await second.CommandAsync()).ToJsonString());
        Assert.Equal(100m, await second.BalanceAsync("Inventory"));

        // AND a later context starts with clean financial inputs rather than a prior case's mutations.
        await using var later = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        Assert.Equal(6, later.Accounts.Count);
        Assert.Equal(0, await later.CountAsync("RecognitionUnits"));
        Assert.Equal(0m, await later.BalanceAsync("Inventory"));
        var destination = new SqlConnectionStringBuilder(first.Journal.Application.WebConnectionString);
        var foreign = new SqlConnectionStringBuilder(later.Journal.Application.WebConnectionString);
        destination.UserID = foreign.UserID;
        destination.Password = foreign.Password;
        destination.ConnectTimeout = 2;
        await using var rejected = new SqlConnection(destination.ConnectionString);
        var error = await Assert.ThrowsAsync<SqlException>(() => rejected.OpenAsync());
        Assert.Contains(error.Number, new[] { 18456, 4060 });
    }

    private static async Task<T> ScalarAsync<T>(PurchaseRecognitionTestContext context, string sql)
    {
        await using var connection = new SqlConnection(context.Journal.Application.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
