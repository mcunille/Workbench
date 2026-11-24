// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditSecurityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ExactReplayRetainsResultButRequiresCurrentCreditAuthority()
    {
        // GIVEN an authentic credit and a configuration version that has advanced since its posting.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var command = await context.CreditCommandAsync(bill, "18");
        var request = Guid.NewGuid();
        var original = await context.ExecuteRawAsync(command.ToJsonString(), request);
        await context.Bills.AdminAsync("UPDATE Accounting.Configurations SET Version=NEWID()");
        // WHEN retried exactly THEN its original authorized receipt is returned despite newer versions.
        Assert.Equal(original.ToJsonString(), (await context.ExecuteRawAsync(command.ToJsonString(), request)).ToJsonString());
        // AND losing credit authority revokes replay access without erasing that receipt.
        await context.Bills.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimValue='SupplierCreditsPost'");
        var denied = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(command.ToJsonString(), request));
        Assert.Equal(51003, denied.Number);
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
    }

    [Fact]
    public async Task ChangedPayloadAndDifferentRequestCannotRepostCredit()
    {
        // GIVEN one successful source and request identity.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var command = await context.CreditCommandAsync(bill, "18");
        var request = Guid.NewGuid();
        await context.ExecuteRawAsync(command.ToJsonString(), request);
        // WHEN the same request changes, or another request tries to claim the same source, THEN both conflict.
        var changed = command.DeepClone().AsObject(); changed["reason"] = "A different review decision";
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(changed.ToJsonString(), request))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(command.ToJsonString()))).Number);
        var revisionAttempt = command.DeepClone().AsObject(); revisionAttempt["creditRevisionId"] = Guid.NewGuid().ToString();
        revisionAttempt["reference"] = Guid.NewGuid().ToString();
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteRawAsync(revisionAttempt.ToJsonString()))).Number);
        Assert.Equal(18m, await context.BalanceAsync(Guid.Parse(command["creditId"]!.GetValue<string>())));
    }

    [Fact]
    public async Task RuntimeCannotDirectlyMutateCreditHistory()
    {
        // GIVEN a credit posted by the authorized source procedure.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        await context.CreditAsync(bill, "18");
        // WHEN a runtime caller attempts ordinary table DML THEN permission denial preserves the source.
        foreach (var table in new[] { "SupplierCredits", "SupplierCreditClaims" })
        {
            await using var command = new SqlCommand($"DELETE FROM Purchasing.{table}", context.Bills.Journal.Connection);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
    }

    [Fact]
    public async Task OtherTenantCannotReadCreditSourceClaimsOrVersions()
    {
        // GIVEN a real credit in this tenant and a separately signed tenant context on the same database.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        await context.CreditAsync(bill, "18");
        await using var other = await context.Bills.Journal.OpenOtherTenantAsync();
        // WHEN the other context uses the newly granted reads THEN RLS hides every source record.
        foreach (var table in new[] { "SupplierCredits", "SupplierCreditClaims", "SupplierCreditVersions" })
        {
            await using var command = new SqlCommand($"SELECT COUNT(*) FROM Purchasing.{table}", other);
            Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericJournalCorrectionCannotDetachSupplierSource(bool refund)
    {
        // GIVEN an authentic credit or receipt and a trusted caller of generic journal correction.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        System.Text.Json.Nodes.JsonObject source;
        Guid item;
        if (refund)
        {
            var deposit = await context.Payments.CommandAsync("100"); await context.Payments.RecordAsync(deposit);
            item = Guid.Parse(deposit["paymentId"]!.GetValue<string>());
            source = await context.RefundAsync([(item, "40")]);
        }
        else
        {
            source = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
            item = Guid.Parse(source["sourceId"]!.GetValue<string>());
        }
        var permission = refund ? "SupplierRefundsRecord" : "SupplierCreditsPost";
        await context.Bills.AdminAsync($$"""
            CREATE PROCEDURE Purchasing.AttemptGenericCreditCorrection
              @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
            AS BEGIN
              SET XACT_ABORT ON;
              BEGIN TRY
                BEGIN TRAN;
                DECLARE @journal uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.journalId')),
                  @config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.configurationVersion'));
                EXEC Accounting.CorrectJournal @ActorId,@SessionId,@RequestId,N'{{permission}}',N'Supplier.GenericAttempt',1,
                  @Command,@journal,@config,'2026-09-19',N'Attempt generic correction',N'{}';
                COMMIT;
              END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
            END;
            """);
        await context.Bills.AdminAsync("GRANT EXECUTE ON Purchasing.AttemptGenericCreditCorrection TO workbench_web");
        var command = new System.Text.Json.Nodes.JsonObject
        {
            ["journalId"] = source["journalIds"]![0]!.DeepClone(),
            ["configurationVersion"] = context.Bills.Journal.ConfigurationVersion.ToString()
        };
        // WHEN the generic owner attempts to invert it THEN the real source-owned movement blocks detachment.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("AttemptGenericCreditCorrection", Guid.NewGuid(), command));
        Assert.Equal(51009, error.Number);
        Assert.Equal(refund ? 60m : 18m, await context.BalanceAsync(item));
        Assert.Equal(refund ? -60m : 0m, await context.BankBalanceAsync());
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.CorrectionGroups"));
    }
}
