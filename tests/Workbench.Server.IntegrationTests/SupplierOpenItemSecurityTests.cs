// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierOpenItemSecurityTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayRequiresCurrentAuthority(bool application)
    {
        // GIVEN an authentic receipt from each independently authorized supplier command.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var input = await context.CommandAsync();
        if (application)
        {
            await context.RecordAsync(input);
            input = await context.Allocation.CommandAsync(Guid.Parse(input["paymentId"]!.ToString()), await context.Allocation.BillAsync());
        }
        var operation = application ? "ApplySupplierFunds" : "RecordSupplierPayment";
        var permission = application ? "SupplierAllocationsManage" : "SupplierPaymentsRecord";
        var request = Guid.NewGuid();
        var first = await ExecuteRawAsync(context, operation, request, input);
        // WHEN operational versions change THEN authorized replay still returns the exact original result.
        await context.Bills.AdminAsync("UPDATE Purchasing.DraftOrders SET UpdatedAtUtc=SYSUTCDATETIME()");
        Assert.Equal(first, await ExecuteRawAsync(context, operation, request, input));
        await context.Bills.AdminAsync($"DELETE [Identity].RoleClaims WHERE ClaimValue='{permission}' AND TenantId='{JournalTestContext.TenantId}'");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        // THEN the receipt confers no authority after the command's own permission is revoked.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync(operation, request, input))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
    }

    internal static async Task<string> ExecuteRawAsync(SupplierPaymentTestContext context, string operation, Guid request, JsonObject input, SqlConnection? connection = null)
    {
        await using var command = new SqlCommand($"EXEC Purchasing.{operation} @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Command=@input", connection ?? context.Allocation.Journal.Connection);
        command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); command.Parameters.AddWithValue("@session", context.Allocation.Journal.SessionId);
        command.Parameters.AddWithValue("@request", request); command.Parameters.AddWithValue("@input", input.ToJsonString());
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedForeignAndMissingSourcesAreIndistinguishable(bool application)
    {
        // GIVEN valid production commands in tenant A and a current separately authorized tenant B principal.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var input = await context.CommandAsync();
        await context.RecordAsync(input);
        if (application)
        {
            input = await context.Allocation.CommandAsync(Guid.Parse(input["paymentId"]!.ToString()), await context.Allocation.BillAsync());
            await context.Allocation.ApplyAsync(input);
        }
        var operation = application ? "ApplySupplierFunds" : "RecordSupplierPayment";
        var session = await AuthorizeOtherTenantAsync(context);
        await using var other = await context.Allocation.Journal.OpenOtherTenantAsync();
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        await using var command = new SqlCommand($"EXEC Purchasing.{operation} @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Command=@input", other);
        command.Parameters.AddWithValue("@actor", AuthTestApplication.OtherTenantUserId);
        command.Parameters.AddWithValue("@session", session);
        command.Parameters.AddWithValue("@request", Guid.NewGuid());
        command.Parameters.AddWithValue("@input", input.ToJsonString());
        // WHEN real foreign identities and then nonexistent identities are submitted with valid authority.
        var foreign = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        input["purchaseOrderId"] = Guid.NewGuid().ToString();
        command.Parameters["@input"].Value = input.ToJsonString();
        var missing = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        // THEN both fail at the same tenant-owned source boundary, never the permission guard.
        Assert.Equal(51009, foreign.Number);
        Assert.Equal(foreign.Number, missing.Number);
        Assert.Equal(foreign.Message, missing.Message);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
    }

    [Theory]
    [InlineData("PreviewSupplierPaymentCorrection")]
    [InlineData("CorrectSupplierPayment")]
    [InlineData("ReverseSupplierApplication")]
    public async Task AuthorizedForeignAndMissingCorrectionSourcesAreIndistinguishable(string operation)
    {
        // GIVEN real tenant A evidence and a separately authorized current tenant B actor.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("100");
        var payment = await context.CommandAsync(); await context.AllocateAsync(payment, bill, "100");
        var posted = await context.RecordAsync(payment);
        var input = operation == "ReverseSupplierApplication"
            ? await SupplierCorrectionFixture.ReverseAsync(context, Guid.Parse(posted["applicationIds"]![0]!.ToString()))
            : await SupplierCorrectionFixture.CorrectionAsync(context, Guid.Parse(payment["paymentId"]!.ToString()));
        var session = await AuthorizeOtherTenantAsync(context);
        await using var other = await context.Allocation.Journal.OpenOtherTenantAsync();
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        var signature = operation == "PreviewSupplierPaymentCorrection" ? "@actor,@session,@input" : "@actor,@session,@request,@input";
        await using var command = new SqlCommand($"EXEC Purchasing.{operation} {signature}", other);
        command.Parameters.AddWithValue("@actor", AuthTestApplication.OtherTenantUserId); command.Parameters.AddWithValue("@session", session);
        command.Parameters.AddWithValue("@request", Guid.NewGuid()); command.Parameters.AddWithValue("@input", input.ToJsonString());
        // WHEN the authorized actor submits existing foreign and nonexistent source contexts.
        var foreign = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        input["purchaseOrderId"] = Guid.NewGuid().ToString();
        input[operation == "ReverseSupplierApplication" ? "applicationId" : "paymentId"] = Guid.NewGuid().ToString();
        command.Parameters["@input"].Value = input.ToJsonString();
        var missing = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        // THEN detail is indistinguishable and no cross-tenant correction or preview mutation survives.
        Assert.Equal(51009, foreign.Number); Assert.Equal(foreign.Number, missing.Number); Assert.Equal(foreign.Message, missing.Message);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
    }

    private static async Task<Guid> AuthorizeOtherTenantAsync(SupplierPaymentTestContext context)
    {
        var session = Guid.NewGuid();
        await PurchaseRecognitionCorrectionTests.AdminAsync(context.Bills.Recognition, """
            INSERT [Identity].UserRoles(TenantId,UserId,RoleId)
              SELECT @tenant,@actor,RoleId FROM Administration.AccountingRoles WHERE TenantId=@tenant AND Kind='Administrator';
            INSERT [Identity].Sessions(Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
              SELECT @session,@tenant,@actor,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME()) FROM [Identity].Users WHERE Id=@actor;
            """, ("@tenant", JournalTestContext.OtherTenantId), ("@actor", AuthTestApplication.OtherTenantUserId), ("@session", session));
        return session;
    }
}
