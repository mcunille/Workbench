// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionSecurityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ConfigurationPermissionAloneCannotAuthorizeSourcePosting()
    {
        // GIVEN an accounting administrator without the disposable source adapter's own permission.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var input = await context.CommandAsync();
        await using var admin = new SqlConnection(context.Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using (var revoke = new SqlCommand("DELETE [Identity].RoleClaims WHERE ClaimValue=N'PurchaseRecognitionFixturePost'", admin))
            await revoke.ExecuteNonQueryAsync();
        // WHEN attempting financial source posting THEN configuration authority is insufficient.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(input.ToJsonString()))).Number);
        Assert.Equal(0, await context.CountAsync("RecognitionSideEvents"));
    }

    [Fact]
    public async Task RuntimeCannotBypassTypedAdapterOrMutateEvidence()
    {
        // GIVEN the real web principal in a disposable database.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        // WHEN bypassing the source adapter or modifying stored evidence THEN SQL denies access.
        foreach (var sql in new[] { "EXEC Purchasing.PostRecognition NULL,NULL,NULL,NULL,NULL", "DELETE Purchasing.RecognitionSideEvents", "UPDATE Purchasing.RecognitionGroupReceipts SET ResultJson='{}'" })
        {
            await using var command = new SqlCommand(sql, context.Connection);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
    }
}
