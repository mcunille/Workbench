// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillSecurityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task AuthorizedForeignTenantCannotSeeBillOrItsHistory()
    {
        // GIVEN posted evidence in tenant A and a current accounting reader in tenant B.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context);
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        var session = Guid.NewGuid();
        await PurchaseRecognitionCorrectionTests.AdminAsync(context.Recognition, """
            INSERT [Identity].UserRoles(TenantId,UserId,RoleId)
              SELECT @tenant,@actor,RoleId FROM Administration.AccountingRoles WHERE TenantId=@tenant AND Kind='Administrator';
            INSERT [Identity].Sessions(Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
              SELECT @session,@tenant,@actor,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME()) FROM [Identity].Users WHERE Id=@actor;
            """, ("@tenant", JournalTestContext.OtherTenantId), ("@actor", AuthTestApplication.OtherTenantUserId), ("@session", session));
        await using var other = await context.Journal.OpenOtherTenantAsync();
        var queries = new Workbench.Server.Purchasing.SupplierBillQueries(other);
        // WHEN foreign and missing identities are probed THEN the response and history reveal neither.
        var foreign = await Assert.ThrowsAsync<SqlException>(() => queries.ReadAsync(AuthTestApplication.OtherTenantUserId, session, Guid.Parse(reviewed["billId"]!.ToString()), default));
        var missing = await Assert.ThrowsAsync<SqlException>(() => queries.ReadAsync(AuthTestApplication.OtherTenantUserId, session, Guid.NewGuid(), default));
        Assert.Equal(51004, foreign.Number); Assert.Equal(foreign.Message, missing.Message);
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => queries.HistoryAsync(AuthTestApplication.OtherTenantUserId, session, Guid.Parse(reviewed["billId"]!.ToString()), 0, 10, default))).Number);
        var postInput = context.PostCommand(reviewed);
        await using (var post = new SqlCommand("EXEC Purchasing.PostSupplierBill @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Command=@input", other))
        {
            post.Parameters.AddWithValue("@actor", AuthTestApplication.OtherTenantUserId); post.Parameters.AddWithValue("@session", session);
            post.Parameters.AddWithValue("@request", Guid.NewGuid()); post.Parameters.AddWithValue("@input", postInput.ToJsonString());
            var foreignWrite = await Assert.ThrowsAsync<SqlException>(() => post.ExecuteScalarAsync());
            postInput["billId"] = Guid.NewGuid().ToString(); post.Parameters["@input"].Value = postInput.ToJsonString();
            var missingWrite = await Assert.ThrowsAsync<SqlException>(() => post.ExecuteScalarAsync());
            Assert.Equal(51004, foreignWrite.Number); Assert.Equal(foreignWrite.Message, missingWrite.Message);
        }
        // AND tenant-qualified foreign keys independently reject a head linked to another tenant's PO.
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => context.AdminAsync($"INSERT Purchasing.SupplierBills(TenantId,Id,PurchaseOrderId,SupplierId,Currency,State) VALUES('{JournalTestContext.OtherTenantId}',NEWID(),'{context.Recognition.PurchaseOrderId}','{context.Recognition.SupplierId}','USD','Draft')"))).Number);
        // AND RLS independently hides every source table even if a future reader receives SELECT.
        foreach (var table in new[] { "SupplierBills", "SupplierBillRevisions", "SupplierBillReviews", "SupplierBillEvidence", "SupplierBillPostings", "SupplierBillPostingEvents", "SupplierBillReceipts" })
        {
            await context.AdminAsync($"GRANT SELECT ON Purchasing.{table} TO workbench_web");
            await using var command = new SqlCommand($"SELECT COUNT(*) FROM Purchasing.{table}", other);
            Assert.Equal(0, await command.ExecuteScalarAsync());
        }
    }

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
    [InlineData("operation", "Create ")]
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

    [Fact]
    public async Task PredecessorMustResolveWithinTenant()
    {
        // GIVEN a bill whose purported predecessor is unavailable in this tenant.
        await using var context = await SupplierBillTestContext.OpenAsync(sqlServer);
        var input = context.DraftCommand();
        input["revision"]!["predecessorBillId"] = Guid.NewGuid().ToString();
        // WHEN the revision is saved THEN no dangling source lineage is accepted.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), input))).Number);
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBills"));
        // AND a real same-tenant predecessor is retained as immutable source evidence.
        var prior = await context.SaveAsync(Guid.NewGuid(), context.DraftCommand("PRO-1"));
        input["revision"]!["predecessorBillId"] = prior["billId"]!.DeepClone();
        Assert.Equal("Draft", (await context.SaveAsync(Guid.NewGuid(), input))["state"]!.GetValue<string>());
    }
}
