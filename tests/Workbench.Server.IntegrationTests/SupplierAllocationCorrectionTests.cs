// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using System.Text.Json.Nodes;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierAllocationCorrectionTests(SqlServerFixture sqlServer, SupplierAllocationCorrectionScenarios scenarios) : IClassFixture<SupplierAllocationCorrectionScenarios>
{
    [Theory]
    [InlineData(true)]
    public async Task ConcurrentUnapplicationsReleaseCapacityExactlyOnce(bool firstWins)
    {
        // GIVEN two independently submitted inverse commands for one actual application.
        await using var prepared = await scenarios.OpenAsync("bill100");
        var context = prepared.Context;
        var bill = Guid.Parse(prepared.Data["bill"]!.GetValue<string>()); var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var funding = Guid.Parse(payment["paymentId"]!.ToString());
        var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill));
        var first = await SupplierCorrectionFixture.ReverseAsync(context, Guid.Parse(applied["applicationIds"]![0]!.ToString()));
        var second = first.DeepClone().AsObject(); second["reason"] = "Second inverse request";
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        Task One() => context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), first);
        Task Two() => context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), second, sibling);
        // WHEN each connection is forced to hold the accounting transaction first.
        var error = await PurchaseRecognitionConcurrencyTests.InOrderAsync(context.Bills.Recognition,
            firstWins ? context.Allocation.Journal.Connection : sibling, firstWins ? sibling : context.Allocation.Journal.Connection,
            firstWins ? One : Two, firstWins ? Two : One);
        // THEN one inverse wins and each capacity is restored once, with original cash untouched.
        Assert.Equal(51009, error?.Number);
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplicationReversals"));
        Assert.Equal(firstWins ? first["reason"]!.ToString() : second["reason"]!.ToString(), await context.Bills.ScalarAsync<string>("SELECT Reason FROM Purchasing.SupplierApplicationReversals"));
        Assert.Equal(100m, await context.Allocation.BalanceAsync(funding)); Assert.Equal(100m, await context.Allocation.BalanceAsync(bill));
        Assert.Equal(-100m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
    }

    [Fact]
    public async Task ExplicitPartialReapplicationIsOneGroupAndPreservesCash()
    {
        // GIVEN a fully applied real deposit and an explicit retained application of forty.
        await using var prepared = await scenarios.OpenAsync("bill100");
        var context = prepared.Context;
        var bill = Guid.Parse(prepared.Data["bill"]!.GetValue<string>());
        var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var funding = Guid.Parse(payment["paymentId"]!.ToString());
        var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill));
        var original = Guid.Parse(applied["applicationIds"]![0]!.ToString());
        var reverse = await SupplierCorrectionFixture.ReverseAsync(context, original);
        var retain = await context.Allocation.CommandAsync(funding, bill, "40", "2026-09-20");
        reverse["reapplications"] = new JsonArray(new JsonObject
        {
            ["fundingItemId"] = funding.ToString(),
            ["expectedFundingItemVersion"] = retain["expectedFundingItemVersion"]!.DeepClone(),
            ["billId"] = bill.ToString(),
            ["itemId"] = bill.ToString(),
            ["expectedItemVersion"] = retain["targets"]![0]!["expectedItemVersion"]!.DeepClone(),
            ["amount"] = "40"
        });
        // WHEN either original rowversion has a valid prefix followed by an extra hex byte.
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        foreach (var field in new[] { "expectedItemVersion", "expectedFundingItemVersion" })
        {
            var malformed = reverse.DeepClone().AsObject();
            var target = malformed["reapplications"]![0]!;
            target[field] = target[field]!.GetValue<string>() + "00";
            // THEN structural rejection leaves every persisted financial and audit row unchanged.
            var error = await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), malformed));
            Assert.Equal(51000, error.Number);
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        }
        // WHEN the complete old application is reversed and the requested portion reapplied atomically.
        var result = await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), reverse);
        // THEN two immutable applications represent the edit and cash has not changed.
        Assert.Equal(60m, await context.Allocation.BalanceAsync(funding));
        Assert.Equal(60m, await context.Allocation.BalanceAsync(bill));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
        Assert.Equal(Guid.Parse(result["groupId"]!.ToString()), await context.Bills.ScalarAsync<Guid>($"SELECT GroupId FROM Purchasing.SupplierApplications WHERE Id<>'{original}'"));
        Assert.Equal(-100m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
        var nested = JsonNode.Parse(await context.Bills.ScalarAsync<string>($"SELECT RequestId requestId,CanonicalInput command,ResultJson result FROM Purchasing.SupplierFinancialReceipts WHERE GroupId='{result["groupId"]}' AND Operation='ApplySupplierFunds' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER"))!;
        Assert.Equal(nested["result"]!.ToString(), await SupplierOpenItemSecurityTests.ExecuteRawAsync(context, "ApplySupplierFunds", Guid.Parse(nested["requestId"]!.ToString()), JsonNode.Parse(nested["command"]!.ToString())!.AsObject()));
        Assert.Equal(DateTimeOffset.Parse(result["recordedAtUtc"]!.ToString()), DateTimeOffset.Parse(JsonNode.Parse(nested["result"]!.ToString())!["recordedAtUtc"]!.ToString()));
    }

    [Fact]
    public async Task GenericJournalCorrectionCannotDetachSupplierEvidence()
    {
        // GIVEN genuine posted supplier evidence and a privileged generic source adapter.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var payment = await context.CommandAsync();
        var posted = await context.RecordAsync(payment);
        await context.Bills.AdminAsync("""
            CREATE PROCEDURE Purchasing.GenericFixtureSupplierCorrection
              @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
            WITH EXECUTE AS OWNER AS BEGIN
              SET XACT_ABORT ON; SET NOCOUNT ON;
              BEGIN TRY
                BEGIN TRAN;
                DECLARE @Journal uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.journalId')),
                  @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.configuration')),@Evidence nvarchar(max);
                SET @Evidence=(SELECT 1 schemaVersion,CONVERT(nvarchar(36),s.SourceRevision) originalSourceRevision,
                  CONVERT(varchar(64),s.SnapshotSha256,2) originalSnapshotSha256 FROM Accounting.SourceEvents s
                  JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id WHERE j.Id=@Journal FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
                DECLARE @Result TABLE(CorrectionId uniqueidentifier,ReversalJournalId uniqueidentifier,ReplacementJournalId uniqueidentifier,ReplacementSourceRevision uniqueidentifier,RecordedAtUtc datetimeoffset);
                INSERT @Result EXEC Accounting.CorrectJournal @ActorId,@SessionId,@RequestId,N'SupplierPaymentsCorrect',N'Supplier.CorrectPayment',1,
                  @Command,@Journal,@Config,'2026-09-20',N'Generic attempt',@Evidence;
                COMMIT;
                SELECT N'{}' ResultJson;
              END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
            END;
            """);
        await context.Bills.AdminAsync("GRANT EXECUTE ON Purchasing.GenericFixtureSupplierCorrection TO workbench_web;");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        // WHEN a generic caller supplies authentic journal evidence and even the owner command label.
        var command = new JsonObject { ["journalId"] = posted["journalIds"]![0]!.DeepClone(), ["configuration"] = context.Allocation.Journal.ConfigurationVersion.ToString() };
        var error = await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("GenericFixtureSupplierCorrection", Guid.NewGuid(), command));
        // THEN source ownership rejects the detached inverse before any mutation.
        Assert.Equal(51009, error.Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReversingApplicationRestoresBothCapacitiesWithoutChangingCash(bool embedded)
    {
        // GIVEN either an embedded or later allocation of an actual payment.
        await using var prepared = await scenarios.OpenAsync("bill100");
        var context = prepared.Context;
        var bill = Guid.Parse(prepared.Data["bill"]!.GetValue<string>());
        var payment = await context.CommandAsync();
        if (embedded) await context.AllocateAsync(payment, bill, "100");
        var posted = await context.RecordAsync(payment);
        var funding = Guid.Parse(payment["paymentId"]!.ToString());
        if (!embedded) posted = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill));
        var application = Guid.Parse(posted["applicationIds"]![0]!.ToString());
        await context.Bills.AdminAsync("UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME(),Version=NEWID(),Name='Later archived control' WHERE Purpose IN('SupplierPayable','SupplierAdvance')");
        // WHEN the complete application is reversed.
        await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), await SupplierCorrectionFixture.ReverseAsync(context, application));
        // THEN both capacities are restored while the original real cash remains.
        Assert.Equal(100m, await context.Allocation.BalanceAsync(bill));
        Assert.Equal(100m, await context.Allocation.BalanceAsync(funding));
        Assert.Equal(-100m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
    }
}
