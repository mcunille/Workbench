// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillSecurityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RevokedManagePermissionRejectsSuccessfulReceiptReplay()
    {
        // GIVEN a successful draft and its original request identity.
        await using var context = await SupplierBillTestContext.OpenAsync(sqlServer);
        var input = context.DraftCommand(); var request = Guid.NewGuid();
        await context.SaveAsync(request, input);
        // WHEN authority is revoked before an exact retry.
        await context.AdminAsync("DELETE [Identity].RoleClaims WHERE ClaimValue=N'SupplierBillsManage'");
        var error = await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(request, input));
        // THEN a durable receipt does not confer continuing permission.
        Assert.Equal(51003, error.Number);
    }

    [Theory]
    [InlineData("billId", "not-a-guid")]
    [InlineData("currency", "USDX")]
    [InlineData("operation", "create")]
    [InlineData("expectedPurchaseOrderVersion", "0x01")]
    [InlineData("unexpected", "override")]
    public async Task MalformedEnvelopeCannotCreateSource(string property, string value)
    {
        // GIVEN a malformed command at the authoritative SQL boundary.
        await using var context = await SupplierBillTestContext.OpenAsync(sqlServer);
        var input = context.DraftCommand(); input[property] = value;
        // WHEN it is submitted THEN validation rejects without success.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), input))).Number);
    }
}
