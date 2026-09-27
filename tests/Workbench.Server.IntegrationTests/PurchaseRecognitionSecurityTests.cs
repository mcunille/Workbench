// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionSecurityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ForeignMatchedUnitIsHiddenAndIndistinguishableFromMissingUnit()
    {
        // GIVEN a matched unit belonging to tenant A and an authorized correction actor in tenant B.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var receipt = await context.CommandAsync(); var invoice = await context.CommandAsync("Invoice");
        receipt["units"]![0]!["sides"]!.AsArray().Add(invoice["units"]![0]!["sides"]![0]!.DeepClone());
        await context.PostAsync(receipt.ToJsonString());
        var correction = await PurchaseRecognitionCorrectionTests.CorrectionAsync(context, receipt, null);
        var otherPo = Guid.NewGuid(); var otherSupplier = Guid.NewGuid(); var session = Guid.NewGuid();
        await PurchaseRecognitionCorrectionTests.AdminAsync(context, """
            INSERT [Identity].UserRoles(TenantId,UserId,RoleId)
              SELECT @tenant,@actor,RoleId FROM Administration.AccountingRoles WHERE TenantId=@tenant AND Kind='Administrator';
            INSERT [Identity].Sessions(Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
              SELECT @session,@tenant,@actor,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME()) FROM [Identity].Users WHERE Id=@actor;
            INSERT Purchasing.Suppliers(Id,TenantId,Name,IsArchived,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId)
              VALUES(@supplier,@tenant,'Other supplier',0,SYSUTCDATETIME(),SYSUTCDATETIME(),@actor,@actor);
            INSERT Purchasing.DraftOrders(Id,TenantId,IsDeleted,State,OrderDate,Revision,SupplierId,Currency,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId)
              VALUES(@po,@tenant,0,'Ordered','2026-01-01',1,@supplier,'USD',4,'{}',SYSUTCDATETIME(),SYSUTCDATETIME(),@actor,@actor);
            """, ("@tenant", JournalTestContext.OtherTenantId), ("@actor", AuthTestApplication.OtherTenantUserId), ("@session", session), ("@po", otherPo), ("@supplier", otherSupplier));
        await using var other = await context.Journal.OpenOtherTenantAsync();
        await using (var read = new SqlCommand("SELECT RowVersion FROM Purchasing.DraftOrders WHERE Id=@id", other))
        {
            read.Parameters.AddWithValue("@id", otherPo);
            correction["expectedPurchaseOrderVersion"] = "0x" + Convert.ToHexString((byte[])(await read.ExecuteScalarAsync())!);
        }
        correction["purchaseOrderId"] = otherPo.ToString();
        async Task<SqlException> RejectAsync()
        {
            await using var command = new SqlCommand("EXEC Purchasing.CorrectFixtureRecognition @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Command=@json", other);
            command.Parameters.AddWithValue("@actor", AuthTestApplication.OtherTenantUserId); command.Parameters.AddWithValue("@session", session);
            command.Parameters.AddWithValue("@request", Guid.NewGuid()); command.Parameters.AddWithValue("@json", correction.ToJsonString());
            return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        }
        // WHEN tenant B probes the real foreign unit and a missing unit THEN neither leaks matched history.
        await using var hidden = new SqlCommand("SELECT COUNT(*) FROM Purchasing.RecognitionMatches", other);
        Assert.Equal(0, await hidden.ExecuteScalarAsync());
        var foreign = await RejectAsync(); correction["unitId"] = Guid.NewGuid().ToString(); var missing = await RejectAsync();
        Assert.Equal(51004, foreign.Number); Assert.Equal(foreign.Number, missing.Number); Assert.Equal(foreign.Message, missing.Message);
        Assert.Equal(1, await context.CountAsync("RecognitionMatches"));
        Assert.Equal(0, await context.CountAsync("RecognitionEventCorrections"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedAuthorityCannotReplayRetainedReceipt(bool correction)
    {
        // GIVEN a successful private source receipt whose source permission is later revoked.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var input = await context.CommandAsync(); var request = Guid.NewGuid();
        await context.PostAsync(input.ToJsonString(), correction ? null : request);
        if (correction)
        {
            input = await PurchaseRecognitionCorrectionTests.CorrectionAsync(context, input, null);
            await context.CorrectAsync(input.ToJsonString(), request);
        }
        await PurchaseRecognitionCorrectionTests.AdminAsync(context, "DELETE [Identity].RoleClaims WHERE ClaimValue=@permission", ("@permission", correction ? "PurchaseRecognitionFixtureCorrect" : "PurchaseRecognitionFixturePost"));
        // WHEN replaying identical input THEN current authority fails before returning stored evidence.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => correction
            ? context.CorrectAsync(input.ToJsonString(), request) : context.PostAsync(input.ToJsonString(), request))).Number);
        Assert.Equal(correction ? 2 : 1, await context.CountAsync("RecognitionGroupReceipts"));
        // AND the internal kernel independently rechecks authority even if a trusted adapter omits its own check.
        await using var admin = new SqlConnection(context.Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await new TenantContextProof(context.Journal.ProofKey).ApplyAsync(admin, JournalTestContext.TenantId, default);
        await using var direct = new SqlCommand($"""
            BEGIN TRY
              BEGIN TRANSACTION;
              EXEC Purchasing.{(correction ? "CorrectRecognition" : "PostRecognition")}
                @ActorId=@actor,@SessionId=@session,@RequestId=@request,@RequiredPermission=@permission,@Command=@input;
              COMMIT;
            END TRY BEGIN CATCH
              IF @@TRANCOUNT>0 ROLLBACK;
              THROW;
            END CATCH;
            """, admin);
        direct.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
        direct.Parameters.AddWithValue("@session", context.Journal.SessionId);
        direct.Parameters.AddWithValue("@request", request);
        direct.Parameters.AddWithValue("@permission", correction ? "PurchaseRecognitionFixtureCorrect" : "PurchaseRecognitionFixturePost");
        direct.Parameters.AddWithValue("@input", input.ToJsonString());
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => direct.ExecuteNonQueryAsync())).Number);
    }

    [Fact]
    public async Task ForeignSourceHasTheSameUnavailableOutcomeAsMissingSource()
    {
        // GIVEN an existing source moved outside the requesting tenant and an unrelated missing ID.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var input = await context.CommandAsync(); var side = input["units"]![0]!["sides"]![0]!;
        await PurchaseRecognitionCorrectionTests.AdminAsync(context, "UPDATE Purchasing.FixtureRecognitionSources SET TenantId=@tenant WHERE Id=@id; UPDATE Purchasing.FixtureRecognitionSourceHeads SET TenantId=@tenant WHERE SourceId=@id", ("@tenant", JournalTestContext.OtherTenantId), ("@id", side["sourceId"]!.GetValue<string>()));
        // WHEN probing both identities THEN the response cannot disclose existence.
        var foreign = await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(input.ToJsonString()));
        side["sourceId"] = Guid.NewGuid().ToString();
        var missing = await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(input.ToJsonString()));
        Assert.Equal(51004, foreign.Number); Assert.Equal(foreign.Number, missing.Number); Assert.Equal(foreign.Message, missing.Message);
        Assert.Equal(0, await context.CountAsync("RecognitionGroupReceipts"));
    }

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
        foreach (var sql in new[] { "EXEC Purchasing.PostRecognition NULL,NULL,NULL,NULL,NULL", "EXEC Purchasing.CorrectRecognition NULL,NULL,NULL,NULL,NULL", "INSERT Purchasing.RecognitionUnits(Id) VALUES(NEWID())", "DELETE Purchasing.RecognitionSideEvents", "UPDATE Purchasing.RecognitionGroupReceipts SET ResultJson='{}'" })
        {
            await using var command = new SqlCommand(sql, context.Connection);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
    }
}
