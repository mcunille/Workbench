// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCorrectionParticipantTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PaidBillReplacementPreservesCash()
    {
        // GIVEN a genuine bill and genuine cash payment, fully applied by production posting.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("300");
        var payment = await context.CommandAsync("300"); await context.AllocateAsync(payment, bill, "300");
        var paid = await context.RecordAsync(payment);
        var funding = Guid.Parse(payment["paymentId"]!.ToString());
        var cashBefore = await CashEvidenceAsync(context);
        var reverse = await SupplierCorrectionFixture.ReverseAsync(context, Guid.Parse(paid["applicationIds"]![0]!.ToString()));
        var reapply = await context.Allocation.CommandAsync(funding, bill, "280", "2026-09-20");
        await InstallAsync(context);
        await BeginAsync(context, bill);
        try
        {
            // WHEN a disposable future bill owner unwinds, replaces its source and explicitly reapplies.
            await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), reverse);
            await ReplaceAsync(context, bill, 280);
            reapply["expectedFundingItemVersion"] = await VersionInOwnerAsync(context, funding);
            reapply["targets"]![0]!["expectedItemVersion"] = await VersionInOwnerAsync(context, bill);
            await context.Allocation.ApplyAsync(reapply);
            await FinishAsync(context);
        }
        catch { await ExecuteAsync(context, "IF @@TRANCOUNT>0 ROLLBACK"); throw; }
        // THEN the real cash source is byte-identical, debt is settled and the excess remains an advance.
        Assert.Equal((0m, 20m, -300m), (await context.Allocation.BalanceAsync(bill), await context.Allocation.BalanceAsync(funding), await BankAsync(context)));
        Assert.Equal(cashBefore, await CashEvidenceAsync(context));
        await AssertOwnerGroupAsync(context);
    }

    [Fact]
    public async Task RefundedCreditReplacementLeavesClearingDebt()
    {
        // GIVEN a disposable credit owner and an independently recorded cash refund of eighteen.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var credit = await context.Allocation.SourceAsync("CreditReceivable", "18");
        var refund = await context.Allocation.SourceAsync("RefundClearing", "18", "2026-09-15", context.Bank);
        await context.Allocation.ParticipantAsync(credit, refund, amount: 18);
        var application = await context.Bills.ScalarAsync<Guid>("SELECT Id FROM Purchasing.SupplierApplications");
        var reverse = await SupplierCorrectionFixture.ReverseAsync(context, application);
        var cashBefore = await CashEvidenceAsync(context);
        await InstallAsync(context);
        await BeginAsync(context, credit);
        try
        {
            // WHEN its future credit owner uses production unapply and the shared allocation participant.
            await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), reverse);
            await ReplaceAsync(context, credit, 10);
            await context.Allocation.ParticipantAsync(credit, refund, amount: 10, date: "2026-09-20");
            await FinishAsync(context);
        }
        catch { await ExecuteAsync(context, "IF @@TRANCOUNT>0 ROLLBACK"); throw; }
        // THEN no repayment is invented: eight remains due in clearing and recorded cash stays eighteen.
        Assert.Equal((0m, 8m, 18m), (await context.Allocation.BalanceAsync(credit), await context.Allocation.BalanceAsync(refund), await BankAsync(context)));
        Assert.Equal(cashBefore, await CashEvidenceAsync(context));
        await AssertOwnerGroupAsync(context);
    }

    private static Task<decimal> BankAsync(SupplierPaymentTestContext context) => context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'");
    private static Task<string> CashEvidenceAsync(SupplierPaymentTestContext context) => context.Bills.ScalarAsync<string>($"SELECT j.*,JSON_QUERY((SELECT l.* FROM Accounting.JournalLines l WHERE l.JournalId=j.Id ORDER BY Ordinal FOR JSON PATH)) lines,JSON_QUERY((SELECT s.* FROM Accounting.SourceEvents s WHERE s.Id=j.SourceEventId FOR JSON PATH)) source FROM Accounting.JournalEntries j WHERE EXISTS(SELECT 1 FROM Accounting.JournalLines l WHERE l.JournalId=j.Id AND l.AccountId='{context.Bank}') ORDER BY j.Sequence FOR JSON PATH");
    private static async Task ExecuteAsync(SupplierPaymentTestContext context, string sql)
    { await using var command = new SqlCommand(sql, context.Allocation.Journal.Connection); await command.ExecuteNonQueryAsync(); }
    private static async Task<string> VersionInOwnerAsync(SupplierPaymentTestContext context, Guid item)
    {
        await using var command = new SqlCommand("SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierItemVersions WHERE ItemId=@item", context.Allocation.Journal.Connection);
        command.Parameters.AddWithValue("@item", item);
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task InstallAsync(SupplierPaymentTestContext context)
    {
        await context.Bills.AdminAsync("""
            CREATE TABLE Purchasing.FixtureSourceCorrections(Id uniqueidentifier PRIMARY KEY,ItemId uniqueidentifier,StartingSequence bigint,OriginalSourceId uniqueidentifier,InverseSourceId uniqueidentifier,ReplacementSourceId uniqueidentifier,RecordedAtUtc datetimeoffset);
            CREATE TABLE Purchasing.FixtureSourceCorrectionMembers(OwnerId uniqueidentifier,GroupId uniqueidentifier,PRIMARY KEY(OwnerId,GroupId));
            """);
        await context.Bills.AdminAsync(AdapterSql);
        await context.Bills.AdminAsync("GRANT EXECUTE ON Purchasing.FixtureCorrectSupplierSource TO workbench_web");
    }
    private static async Task BeginAsync(SupplierPaymentTestContext context, Guid item)
    {
        await ExecuteAsync(context, "BEGIN TRAN");
        await context.Bills.ExecuteAsync("FixtureCorrectSupplierSource", Guid.NewGuid(), new JsonObject { ["step"] = "begin", ["itemId"] = item.ToString() });
    }
    private static Task<JsonObject> ReplaceAsync(SupplierPaymentTestContext context, Guid item, decimal amount) => context.Bills.ExecuteAsync("FixtureCorrectSupplierSource", Guid.NewGuid(),
        new JsonObject { ["step"] = "replace", ["itemId"] = item.ToString(), ["amount"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture), ["configuration"] = context.Allocation.Journal.ConfigurationVersion.ToString() });
    private static async Task FinishAsync(SupplierPaymentTestContext context)
    {
        await context.Bills.ExecuteAsync("FixtureCorrectSupplierSource", Guid.NewGuid(), new JsonObject { ["step"] = "finish" });
        await ExecuteAsync(context, "COMMIT");
    }
    private static async Task AssertOwnerGroupAsync(SupplierPaymentTestContext context)
    {
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.FixtureSourceCorrections WHERE OriginalSourceId IS NOT NULL AND InverseSourceId IS NOT NULL AND ReplacementSourceId IS NOT NULL"));
        Assert.Equal(3, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.FixtureSourceCorrectionMembers"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.FixtureSourceCorrectionMembers m JOIN Purchasing.FixtureSourceCorrections o ON o.Id=m.OwnerId JOIN Purchasing.SupplierFinancialGroups g ON g.Id=m.GroupId JOIN Purchasing.SupplierItemMovements e ON e.GroupId=g.Id JOIN Accounting.SourceEvents s ON s.Id=e.SourceEventId JOIN Accounting.JournalEntries j ON j.SourceEventId=s.Id WHERE g.RecordedAtUtc<>o.RecordedAtUtc OR e.RecordedAtUtc<>o.RecordedAtUtc OR s.RecordedAtUtc<>o.RecordedAtUtc OR j.RecordedAtUtc<>o.RecordedAtUtc"));
    }

    // This disposable future source owner defines only source replacement evidence and atomic membership.
    // Allocation and unapplication decisions remain in real production participants. It is not a bill/credit API.
    // The existing open-item identity is retained here; future public owner revision/projection contracts are out of scope.
    private const string AdapterSql = """
        CREATE PROCEDURE Purchasing.FixtureCorrectSupplierSource @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
        WITH EXECUTE AS OWNER AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Resource nvarchar(255),
            @Step nvarchar(20)=JSON_VALUE(@Command,'$.step'),@Item uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.itemId')),
            @Owner uniqueidentifier,@Now datetimeoffset,@Start bigint;
          IF @@TRANCOUNT=0 THROW 51990,'Fixture source requires the outer owner transaction.',1;
          SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
          EXEC sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction';
          EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
          IF @Step='begin'
          BEGIN
            INSERT Purchasing.FixtureSourceCorrections(Id,ItemId,StartingSequence,RecordedAtUtc)
              SELECT NEWID(),@Item,COALESCE(MAX(Sequence),0),SYSUTCDATETIME() FROM Accounting.JournalEntries WHERE TenantId=@Tenant;
          END;
          SELECT @Owner=Id,@Now=RecordedAtUtc,@Start=StartingSequence FROM Purchasing.FixtureSourceCorrections;
          IF @Step='replace'
          BEGIN
            DECLARE @OriginalSource uniqueidentifier,@OriginalJournal uniqueidentifier,@OriginalAmount decimal(28,4),@Ordinal int,
              @Replacement decimal(28,4)=CONVERT(decimal(28,4),JSON_VALUE(@Command,'$.amount')),
              @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.configuration')),
              @Group uniqueidentifier=NEWID(),@Revision uniqueidentifier=NEWID(),@Lines nvarchar(max),@Snapshot nvarchar(max),@Phase int=0;
            SELECT @OriginalSource=m.SourceEventId,@OriginalJournal=a.JournalId,@OriginalAmount=m.Amount,@Ordinal=a.Ordinal
              FROM Purchasing.SupplierItemMovements m JOIN Purchasing.SupplierControlAttributions a ON a.MovementId=m.Id AND a.TenantId=m.TenantId
              WHERE m.TenantId=@Tenant AND m.ItemId=@Item AND m.EventKind='Open';
            IF @OriginalSource IS NULL OR @Replacement<=0 THROW 51990,'Fixture source evidence is incomplete.',1;
            INSERT Purchasing.SupplierFinancialGroups VALUES(@Tenant,@Group,'CorrectPayment',@Item,@Now);
            DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
            DECLARE @Effects TABLE(Id uniqueidentifier,SourceId uniqueidentifier,JournalId uniqueidentifier,Amount decimal(28,4),Kind varchar(24));
            WHILE @Phase<2
            BEGIN
              DELETE @Posted;
              SET @Lines=(SELECT Ordinal ordinal,AccountId accountId,AccountVersion accountVersion,
                CONVERT(nvarchar(60),CONVERT(decimal(28,4),CASE WHEN @Phase=0 THEN Credit ELSE Debit*@Replacement/@OriginalAmount END)) debit,
                CONVERT(nvarchar(60),CONVERT(decimal(28,4),CASE WHEN @Phase=0 THEN Debit ELSE Credit*@Replacement/@OriginalAmount END)) credit
                FROM Accounting.JournalLines WHERE TenantId=@Tenant AND JournalId=@OriginalJournal ORDER BY Ordinal FOR JSON PATH);
              SET @Snapshot=(SELECT @Owner ownerId,@OriginalSource originalSourceId,@Item itemId,@OriginalJournal originalJournalId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              DECLARE @Request uniqueidentifier=NEWID(),@Event nvarchar(40)=CASE WHEN @Phase=0 THEN 'Inverse' ELSE 'Replacement' END;
              INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@Request,N'SupplierAllocationsManage',N'Fixture.SourceCorrection',1,@Command,
                N'FixtureSupplierSourceCorrection',@Item,@Revision,@Event,1,@Config,N'USD','2026-09-20','2026-09-20','2026-09-20',NULL,NULL,@Snapshot,@Lines;
              INSERT @Effects SELECT NEWID(),SourceEventId,JournalId,CASE WHEN @Phase=0 THEN -@OriginalAmount ELSE @Replacement END,
                CASE WHEN @Phase=0 THEN 'ReverseSource' ELSE 'Open' END FROM @Posted;
              IF @Phase=0 UPDATE Purchasing.FixtureSourceCorrections SET OriginalSourceId=@OriginalSource,InverseSourceId=(SELECT SourceEventId FROM @Posted) WHERE Id=@Owner;
              ELSE UPDATE Purchasing.FixtureSourceCorrections SET ReplacementSourceId=(SELECT SourceEventId FROM @Posted) WHERE Id=@Owner;
              SET @Phase+=1;
            END;
            DECLARE @Movements nvarchar(max)=(SELECT @Item itemId,@Group groupId,'2026-09-20' postingDate,CONVERT(nvarchar(60),Amount) amount FROM @Effects FOR JSON PATH);
            EXEC Purchasing.AssertSupplierAvailability @Tenant,@Movements;
            INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
              SELECT @Tenant,Id,@Item,@Group,Kind,SourceId,'2026-09-20',Amount,@Now FROM @Effects;
            INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
              SELECT @Tenant,NEWID(),@Group,e.Id,e.JournalId,@Ordinal,l.AccountId,l.AccountVersion,l.AccountPurpose,e.Amount FROM @Effects e
              JOIN Accounting.JournalLines l ON l.TenantId=@Tenant AND l.JournalId=e.JournalId AND l.Ordinal=@Ordinal;
            UPDATE Purchasing.SupplierItemVersions SET ItemId=ItemId WHERE TenantId=@Tenant AND ItemId=@Item;
          END;
          IF @Step='finish'
          BEGIN
            INSERT Purchasing.FixtureSourceCorrectionMembers SELECT DISTINCT @Owner,m.GroupId FROM Purchasing.SupplierItemMovements m
              JOIN Accounting.JournalEntries j ON j.TenantId=m.TenantId AND j.SourceEventId=m.SourceEventId WHERE j.TenantId=@Tenant AND j.Sequence>@Start;
            UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id IN(SELECT SourceEventId FROM Accounting.JournalEntries WHERE TenantId=@Tenant AND Sequence>@Start);
            UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND JournalId IN(SELECT Id FROM Accounting.JournalEntries WHERE TenantId=@Tenant AND Sequence>@Start);
            UPDATE Security.TenantSecurityAuditEvents SET OccurredAtUtc=@Now WHERE TenantId=@Tenant AND Action='Accounting.PostJournal'
              AND TargetId IN(SELECT Id FROM Accounting.JournalEntries WHERE TenantId=@Tenant AND Sequence>@Start);
            UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Sequence>@Start;
            UPDATE g SET RecordedAtUtc=@Now FROM Purchasing.SupplierFinancialGroups g JOIN Purchasing.FixtureSourceCorrectionMembers m ON m.GroupId=g.Id WHERE m.OwnerId=@Owner;
            UPDATE e SET RecordedAtUtc=@Now FROM Purchasing.SupplierItemMovements e JOIN Purchasing.FixtureSourceCorrectionMembers m ON m.GroupId=e.GroupId WHERE m.OwnerId=@Owner;
            UPDATE a SET RecordedAtUtc=@Now FROM Purchasing.SupplierApplications a JOIN Purchasing.FixtureSourceCorrectionMembers m ON m.GroupId=a.GroupId WHERE m.OwnerId=@Owner;
            UPDATE a SET RecordedAtUtc=@Now FROM Purchasing.SupplierApplicationReversals a JOIN Purchasing.FixtureSourceCorrectionMembers m ON m.GroupId=a.GroupId WHERE m.OwnerId=@Owner;
            UPDATE r SET RecordedAtUtc=@Now,ResultJson=JSON_MODIFY(ResultJson,'$.recordedAtUtc',CONVERT(nvarchar(40),@Now,127))
              FROM Purchasing.SupplierFinancialReceipts r JOIN Purchasing.FixtureSourceCorrectionMembers m ON m.GroupId=r.GroupId WHERE m.OwnerId=@Owner;
          END;
          SELECT N'{}' ResultJson;
        END;
        """;
}
