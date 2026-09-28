// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed class SupplierAllocationTestContext(SupplierOpenItemTestContext items) : IAsyncDisposable
{
    public SupplierOpenItemTestContext Items { get; } = items;
    public SupplierBillTestContext Bills => Items.Bills;
    public JournalTestContext Journal => Items.Journal;
    public static async Task<SupplierAllocationTestContext> OpenAsync(SqlServerFixture fixture)
    {
        var context = new SupplierAllocationTestContext(await SupplierOpenItemTestContext.OpenAsync(fixture));
        try
        {
            await context.Bills.AdminAsync("""
                GRANT EXECUTE ON Purchasing.ReviewSupplierBill TO workbench_web;
                GRANT EXECUTE ON Purchasing.PostSupplierBill TO workbench_web;
                GRANT EXECUTE ON Purchasing.ApplySupplierFunds TO workbench_web;
                INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
                  SELECT TenantId,RoleId,N'workbench/permission',N'SupplierAllocationsManage'
                  FROM Administration.AccountingRoles WHERE Kind='Administrator';
                """);
            await context.Bills.AdminAsync(SeedSql);
            await context.Bills.AdminAsync("GRANT EXECUTE ON Purchasing.SeedFixtureSupplierCapacity TO workbench_web;");
            await context.Bills.AdminAsync(ParticipantSql);
            await context.Bills.AdminAsync("GRANT EXECUTE ON Purchasing.ApplyFixtureSupplierParticipant TO workbench_web;");
            await context.Bills.AdminAsync(ReleaseSeedSql);
            await context.Bills.AdminAsync("GRANT EXECUTE ON Purchasing.SeedFixtureSupplierRelease TO workbench_web;");
            return context;
        }
        catch { await context.DisposeAsync(); throw; }
    }

    public async Task<Guid> BillAsync(string amount = "150", string date = "2026-09-15")
    {
        var draft = Bills.CompleteDraft(Guid.NewGuid().ToString());
        var revision = draft["revision"]!.AsObject();
        revision["total"] = amount;
        revision["postingDate"] = date;
        revision["units"]![0]!["components"]![0]!["amount"] = amount;
        revision["units"]![0]!["components"]![1]!["amount"] = "0";
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(Bills, draft);
        await Bills.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), Bills.PostCommand(reviewed));
        return Guid.Parse(reviewed["billId"]!.GetValue<string>());
    }

    // These disposable source participants establish inputs only. Assertions always
    // exercise production Apply/availability; no test-written command receipts exist.
    public async Task<Guid> SourceAsync(string kind = "Advance", string amount = "100", string date = "2026-09-10")
    {
        var purpose = kind switch { "Advance" => "SupplierAdvance", "CreditReceivable" => "SupplierCreditReceivable", _ => "SupplierRefundClearing" };
        var account = await Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts",
            new JsonArray(new JsonObject
            {
                ["code"] = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
                ["name"] = purpose,
                ["type"] = kind == "RefundClearing" ? "Liability" : "Asset",
                ["purpose"] = purpose
            }).ToJsonString());
        var accountId = Guid.Parse(JsonNode.Parse(account.Ids)![0]!.GetValue<string>());
        var item = Guid.NewGuid();
        await using var sql = new SqlCommand("Purchasing.SeedFixtureSupplierCapacity", Journal.Connection) { CommandType = System.Data.CommandType.StoredProcedure };
        sql.Parameters.AddWithValue("@ActorId", JournalTestContext.ActorId);
        sql.Parameters.AddWithValue("@SessionId", Journal.SessionId);
        sql.Parameters.AddWithValue("@ItemId", item);
        sql.Parameters.AddWithValue("@Kind", kind);
        sql.Parameters.AddWithValue("@Amount", decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));
        sql.Parameters.AddWithValue("@Date", date);
        sql.Parameters.AddWithValue("@PoId", Items.Recognition.PurchaseOrderId);
        sql.Parameters.AddWithValue("@SupplierId", Items.Recognition.SupplierId);
        sql.Parameters.AddWithValue("@AccountId", accountId);
        sql.Parameters.AddWithValue("@OtherAccountId", Items.Recognition.Accounts["Prepayment"]);
        sql.Parameters.AddWithValue("@Config", Journal.ConfigurationVersion);
        await sql.ExecuteNonQueryAsync();
        return item;
    }

    public async Task<JsonObject> CommandAsync(Guid funding, Guid debt, string amount = "100", string date = "2026-09-16") => new()
    {
        ["schemaVersion"] = 1,
        ["operation"] = "ApplySupplierFunds",
        ["expectedConfigurationVersion"] = Journal.ConfigurationVersion.ToString(),
        ["purchaseOrderId"] = Items.Recognition.PurchaseOrderId.ToString(),
        ["expectedPurchaseOrderVersion"] = Items.Recognition.PurchaseOrderVersion,
        ["supplierId"] = Items.Recognition.SupplierId.ToString(),
        ["currency"] = "USD",
        ["postingDate"] = date,
        ["fundingItemId"] = funding.ToString(),
        ["expectedFundingItemVersion"] = await VersionAsync(funding),
        ["targets"] = new JsonArray(new JsonObject
        {
            ["billId"] = debt.ToString(),
            ["itemId"] = debt.ToString(),
            ["expectedItemVersion"] = await VersionAsync(debt),
            ["amount"] = amount
        })
    };
    public Task<string> VersionAsync(Guid item) => Bills.ScalarAsync<string>($"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierItemVersions WHERE ItemId='{item}'");
    public Task<decimal> BalanceAsync(Guid item) => Bills.ScalarAsync<decimal>($"SELECT COALESCE(SUM(Amount),0) FROM Purchasing.SupplierItemMovements WHERE ItemId='{item}'");
    public Task<JsonObject> ApplyAsync(JsonObject command, Guid? request = null) => Bills.ExecuteAsync("ApplySupplierFunds", request ?? Guid.NewGuid(), command);
    public async Task ParticipantAsync(Guid funding, Guid debt, bool invalidProof = false)
    {
        await using var command = new SqlCommand("EXEC Purchasing.ApplyFixtureSupplierParticipant @ActorId=@actor,@SessionId=@session,@Funding=@funding,@Debt=@debt,@Config=@config,@InvalidProof=@invalid", Journal.Connection);
        command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); command.Parameters.AddWithValue("@session", Journal.SessionId);
        command.Parameters.AddWithValue("@funding", funding); command.Parameters.AddWithValue("@debt", debt);
        command.Parameters.AddWithValue("@config", Journal.ConfigurationVersion); command.Parameters.AddWithValue("@invalid", invalidProof);
        await command.ExecuteNonQueryAsync();
    }
    public ValueTask DisposeAsync() => Items.DisposeAsync();

    public async Task SeedLaterReleaseAsync()
    {
        await using var command = new SqlCommand("EXEC Purchasing.SeedFixtureSupplierRelease @ActorId=@actor,@SessionId=@session,@Config=@config", Journal.Connection);
        command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); command.Parameters.AddWithValue("@session", Journal.SessionId);
        command.Parameters.AddWithValue("@config", Journal.ConfigurationVersion);
        await command.ExecuteNonQueryAsync();
    }

    // Seeds an already recorded release for historical availability tests. This is
    // disposable initial-state support, not the production reversal command or its test.
    private const string ReleaseSeedSql = """
        CREATE PROCEDURE Purchasing.SeedFixtureSupplierRelease @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Config uniqueidentifier
        WITH EXECUTE AS OWNER AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRAN;
            DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Resource nvarchar(255),
              @Id uniqueidentifier=NEWID(),@Request uniqueidentifier=NEWID(),@Group uniqueidentifier=NEWID(),@Application uniqueidentifier,
              @Lines nvarchar(max),@Now datetimeoffset,@Journal uniqueidentifier,@Source uniqueidentifier;
            SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
            EXEC sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction';
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
            SELECT @Application=Id FROM Purchasing.SupplierApplications WHERE TenantId=@Tenant;
            IF (SELECT COUNT(*) FROM Purchasing.SupplierApplications WHERE TenantId=@Tenant)<>1 THROW 51000,'Release seed requires one application.',1;
            SET @Lines=(SELECT l.Ordinal ordinal,l.AccountId accountId,account.Version accountVersion,
              CONVERT(nvarchar(60),l.Credit) debit,CONVERT(nvarchar(60),l.Debit) credit
              FROM Purchasing.SupplierApplications a
              JOIN Purchasing.SupplierItemMovements m ON m.TenantId=a.TenantId AND m.GroupId=a.GroupId AND m.ItemId=a.DebtItemId
              JOIN Accounting.JournalEntries j ON j.TenantId=m.TenantId AND j.SourceEventId=m.SourceEventId
              JOIN Accounting.JournalLines l ON l.TenantId=j.TenantId AND l.JournalId=j.Id
              JOIN Accounting.Accounts account ON account.TenantId=l.TenantId AND account.Id=l.AccountId
              WHERE a.TenantId=@Tenant AND a.Id=@Application FOR JSON PATH);
            DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
            INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@Request,N'SupplierAllocationsManage',N'Fixture.Release',1,N'{}',
              N'FixtureSupplierRelease',@Id,@Id,N'Release',1,@Config,N'USD','2026-09-22','2026-09-22','2026-09-22',NULL,NULL,N'{}',@Lines;
            SELECT @Now=RecordedAtUtc,@Journal=JournalId,@Source=SourceEventId FROM @Posted;
            INSERT Purchasing.SupplierFinancialGroups VALUES(@Tenant,@Group,'ReverseApplication',@Id,@Now);
            INSERT Purchasing.SupplierApplicationReversals(TenantId,Id,GroupId,ApplicationId,PostingDate,Reason,ActorId,RecordedAtUtc)
              VALUES(@Tenant,@Id,@Group,@Application,'2026-09-22','Historical seed',@ActorId,@Now);
            DECLARE @Effects TABLE(Id uniqueidentifier,ItemId uniqueidentifier,Ordinal int,Amount decimal(28,4));
            INSERT @Effects SELECT NEWID(),items.ItemId,items.Ordinal,a.Amount FROM Purchasing.SupplierApplications a
              CROSS APPLY(VALUES(a.DebtItemId,1),(a.FundingItemId,2)) items(ItemId,Ordinal) WHERE a.TenantId=@Tenant AND a.Id=@Application;
            INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
              SELECT @Tenant,Id,ItemId,@Group,'ReverseApplication',@Source,'2026-09-22',Amount,@Now FROM @Effects;
            INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
              SELECT @Tenant,NEWID(),@Group,e.Id,@Journal,e.Ordinal,l.AccountId,l.AccountVersion,l.AccountPurpose,e.Amount
              FROM @Effects e JOIN Accounting.JournalLines l ON l.TenantId=@Tenant AND l.JournalId=@Journal AND l.Ordinal=e.Ordinal;
            UPDATE v SET ItemId=v.ItemId FROM Purchasing.SupplierItemVersions v JOIN @Effects e ON e.ItemId=v.ItemId WHERE v.TenantId=@Tenant;
            COMMIT;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;

    private const string ParticipantSql = """
        CREATE PROCEDURE Purchasing.ApplyFixtureSupplierParticipant @ActorId uniqueidentifier,@SessionId uniqueidentifier,
          @Funding uniqueidentifier,@Debt uniqueidentifier,@Config uniqueidentifier,@InvalidProof bit
        WITH EXECUTE AS OWNER AS BEGIN
          SET XACT_ABORT ON; SET NOCOUNT ON;
          BEGIN TRY
            BEGIN TRAN;
            DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Resource nvarchar(255),
              @Application uniqueidentifier=NEWID(),@Group uniqueidentifier=NEWID(),@Request uniqueidentifier=NEWID(),
              @Lines nvarchar(max),@Proof nvarchar(max),@Snapshot nvarchar(max),@Events nvarchar(max),@Now datetimeoffset;
            SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
            EXEC sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction';
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
            SET @Lines=(SELECT ordinal,c.AccountId accountId,c.AccountVersion accountVersion,
              CASE WHEN ordinal=1 THEN '100' ELSE '0' END debit,CASE WHEN ordinal=2 THEN '100' ELSE '0' END credit
              FROM(VALUES(1,@Debt),(2,@Funding)) e(ordinal,itemId) CROSS APPLY Purchasing.SupplierItemControl(@Tenant,itemId) c FOR JSON PATH);
            SET @Proof=(SELECT ordinal,CASE WHEN @InvalidProof=1 AND ordinal=1 THEN NEWID() ELSE itemId END itemId
              FROM(VALUES(1,@Debt),(2,@Funding)) e(ordinal,itemId) FOR JSON PATH);
            SET @Snapshot=(SELECT @Application applicationId,@Group groupId,@Funding fundingItemId,@Debt debtItemId,'100' amount FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
            INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@Request,N'SupplierAllocationsManage',N'Supplier.Apply',1,N'{}',
              N'SupplierApplication',@Application,@Group,N'Apply',1,@Config,N'USD','2026-09-16','2026-09-16','2026-09-16',NULL,NULL,@Snapshot,@Lines,@Proof;
            SELECT @Now=RecordedAtUtc FROM @Posted;
            SET @Events=(SELECT @Application applicationId,JournalId journalId FROM @Posted FOR JSON PATH);
            EXEC Purchasing.AppendSupplierEventGroup @Tenant,@Group,@Now,@Events;
            COMMIT;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;

    private const string SeedSql = """
        CREATE PROCEDURE Purchasing.SeedFixtureSupplierCapacity
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@ItemId uniqueidentifier,@Kind varchar(24),
          @Amount decimal(28,4),@Date date,@PoId uniqueidentifier,@SupplierId uniqueidentifier,
          @AccountId uniqueidentifier,@OtherAccountId uniqueidentifier,@Config uniqueidentifier
        WITH EXECUTE AS OWNER AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRAN;
            DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),
              @Resource nvarchar(255),@Request uniqueidentifier=NEWID(),@Lines nvarchar(max),@Now datetimeoffset,
              @Journal uniqueidentifier,@Source uniqueidentifier,@Purpose nvarchar(40);
            SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
            EXEC sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction';
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
            SET @Lines=(SELECT ordinal,a.Id accountId,a.Version accountVersion,
              CONVERT(nvarchar(60),CASE WHEN (@Kind='RefundClearing' AND ordinal=2) OR (@Kind<>'RefundClearing' AND ordinal=1) THEN @Amount ELSE 0 END) debit,
              CONVERT(nvarchar(60),CASE WHEN (@Kind='RefundClearing' AND ordinal=1) OR (@Kind<>'RefundClearing' AND ordinal=2) THEN @Amount ELSE 0 END) credit
              FROM (VALUES(1,@AccountId),(2,@OtherAccountId)) v(ordinal,id)
              JOIN Accounting.Accounts a ON a.TenantId=@Tenant AND a.Id=v.id FOR JSON PATH);
            DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
            INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@Request,N'SupplierAllocationsManage',N'Fixture.Capacity',1,N'{}',
              N'FixtureSupplierCapacity',@ItemId,@ItemId,N'Open',1,@Config,N'USD',@Date,@Date,@Date,NULL,NULL,N'{}',@Lines;
            SELECT @Now=RecordedAtUtc,@Journal=JournalId,@Source=SourceEventId FROM @Posted;
            INSERT Purchasing.SupplierFinancialGroups VALUES(@Tenant,@ItemId,'RecordPayment',@ItemId,@Now);
            INSERT Purchasing.SupplierOpenItems(TenantId,Id,Kind,SupplierId,PurchaseOrderId,Currency,SourceKind,SourceId,SourceRevisionId,SourcePostingDate,SourceSnapshotJson,RecordedAtUtc)
              VALUES(@Tenant,@ItemId,@Kind,@SupplierId,@PoId,'USD','FixtureCapacity',@ItemId,@ItemId,@Date,'{}',@Now);
            INSERT Purchasing.SupplierItemVersions(TenantId,ItemId) VALUES(@Tenant,@ItemId);
            INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
              VALUES(@Tenant,@ItemId,@ItemId,@ItemId,'Open',@Source,@Date,@Amount,@Now);
            INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
              SELECT @Tenant,@ItemId,@ItemId,@ItemId,@Journal,1,AccountId,AccountVersion,AccountPurpose,@Amount
              FROM Accounting.JournalLines WHERE TenantId=@Tenant AND JournalId=@Journal AND Ordinal=1;
            COMMIT;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
