// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditCorrectionSecurityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task SupplementAddedAfterPreviewInvalidatesTheInheritedEvidencePlan()
    {
        // GIVEN a real credit, an available supplement and a correction preview before that supplement is linked.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var source = await context.CreditAsync(bill, "18");
        var original = Guid.Parse(source["sourceId"]!.GetValue<string>());
        var document = await context.Bills.SeedDocumentAsync();
        await FinancialEvidenceSecurityTests.InstallAdapterAsync(context.Bills);
        var replacement = await context.CreditCommandAsync(bill, "10", "2026-09-20");
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        await context.CaptureCorrectionPlanAsync(command);
        // AND the genuine BK-07 source owner appends the supplement and its hold after preview.
        var addition = await FinancialEvidenceSecurityTests.CommandAsync(context.Bills, original, (document.DocumentId, document.RevisionId));
        await FinancialEvidenceSecurityTests.AppendAsync(context.Bills, addition, Guid.NewGuid());
        // WHEN the inherited evidence graph has changed THEN the stale correction conflicts without erasing the supplement.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Credit", command))).Number);
        Assert.Equal(18m, await context.BalanceAsync(original));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceAdditions"));
    }

    [Fact]
    public async Task AuthenticPendingParentRejectsDifferentSettlementDate()
    {
        // GIVEN an actual correction whose real parent will reserve an exact release composition.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var source = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
        var original = Guid.Parse(source["sourceId"]!.GetValue<string>());
        var refund = await context.RefundAsync([(original, "18")]);
        var debt = Guid.Parse(refund["settlementIds"]![0]!.GetValue<string>());
        var cash = await context.CashEvidenceAsync();
        // This clone-only interceptor probes the real protected helper while the real parent is
        // pending. It creates no receipt/header/source; rejection must roll back the actual parent.
        await context.Bills.AdminAsync("""
            DECLARE @definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.ReleaseSupplierCashSettlement')),
              @probe nvarchar(max)=N'
              DECLARE @probeGroup uniqueidentifier,@probeCorrection uniqueidentifier,@probeNow datetimeoffset,
                @probePlan nvarchar(max),
                @probeDetails nvarchar(max)=(SELECT DATEADD(day,-1,@PostingDate) postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@OwnerRequestId,N''ReleaseSettlement'',@SettlementId,@probeDetails,
                @probeGroup OUTPUT,@probeCorrection OUTPUT,@probeNow OUTPUT,@probePlan OUTPUT;
              ';
            IF @definition IS NULL OR CHARINDEX(N'SET NOCOUNT ON;',@definition)=0
              THROW 51000,'Expected source release participant unavailable.',1;
            SET @definition=REPLACE(@definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @definition=REPLACE(@definition,N'SET NOCOUNT ON;',N'SET NOCOUNT ON;'+@probe);
            EXEC sys.sp_executesql @definition;
            """);
        var command = await context.CorrectionCommandAsync("Credit", original, null);
        await context.CaptureCorrectionPlanAsync(command);
        // WHEN the authenticated parent attempts altered child intent THEN the complete operation rejects and rolls back.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Credit", command))).Number);
        Assert.Equal(cash, await context.CashEvidenceAsync());
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierOpenItems WHERE Id='{debt}'"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections"));
        Assert.Equal(0m, await context.BalanceAsync(original));
    }

    [Theory]
    [InlineData("SupplierCreditsPost", true, false)]
    [InlineData("SupplierCreditsPost", true, true)]
    [InlineData("SupplierAllocationsManage", false, false)]
    [InlineData("SupplierAllocationsManage", false, true)]
    public async Task PreviewAndReplayRequireSuccessorAndSettlementReleaseAuthorities(string permission, bool replace, bool replay)
    {
        // GIVEN a genuine refunded credit and complete correction intent.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var source = await context.CreditAsync(bill, "18");
        var original = Guid.Parse(source["sourceId"]!.GetValue<string>());
        await context.RefundAsync([(original, "18")]);
        var replacement = replace ? await context.CreditCommandAsync(bill, "10", "2026-09-20") : null;
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        await context.CaptureCorrectionPlanAsync(command);
        var request = Guid.NewGuid();
        var result = replay ? await context.CorrectAsync("Credit", command, request) : null;
        await context.Bills.AdminAsync($"DELETE [Identity].RoleClaims WHERE ClaimValue='{permission}'");
        // WHEN an authority required by that composition is revoked THEN even exact replay rejects.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => replay
            ? context.CorrectAsync("Credit", command, request) : context.PreviewCorrectionAsync("Credit", command))).Number);
        Assert.Equal(replay ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections"));
        if (result is not null)
            Assert.Equal(Guid.Parse(result["groupId"]!.GetValue<string>()), await context.Bills.ScalarAsync<Guid>(
                $"SELECT GroupId FROM Purchasing.SupplierSourceCorrections WHERE SourceId='{original}'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedDependencyAfterPreviewConflicts(bool refund)
    {
        // GIVEN a deterministic preview that reserves no source, journal or success receipt.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var source = await context.CreditAsync(bill, "18"); var original = Guid.Parse(source["sourceId"]!.GetValue<string>());
        var command = await context.CorrectionCommandAsync("Credit", original, null);
        var groups = await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialGroups");
        var preview = await context.PreviewCorrectionAsync("Credit", command);
        Assert.Equal(preview.ToJsonString(), (await context.PreviewCorrectionAsync("Credit", command)).ToJsonString());
        Assert.Equal(groups, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialGroups"));
        command["expectedPlanFingerprint"] = preview["planFingerprint"]!.DeepClone();
        // AND a real allocation or refund adds a dependency after preview.
        if (refund) await context.RefundAsync([(original, "5")]);
        else await context.Payments.Allocation.ApplyAsync(await context.Payments.Allocation.CommandAsync(original, bill, "5", "2026-09-19"));
        var before = await context.CreditEvidenceAsync(original); var cash = await context.CashEvidenceAsync();
        // WHEN stale intent is executed THEN the whole correction conflicts without effects.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Credit", command))).Number);
        Assert.Equal(before, await context.CreditEvidenceAsync(original)); Assert.Equal(cash, await context.CashEvidenceAsync());
        Assert.Equal(13m, await context.BalanceAsync(original));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections"));
    }

    [Fact]
    public async Task ExactCorrectionReplayIgnoresNewVersionsButRequiresCurrentPermission()
    {
        // GIVEN an explicit source reversal that has committed and changed the source version.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var source = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
        var original = Guid.Parse(source["sourceId"]!.GetValue<string>());
        var command = await context.CorrectionCommandAsync("Credit", original, null);
        await context.CaptureCorrectionPlanAsync(command); var request = Guid.NewGuid();
        var posted = await context.CorrectAsync("Credit", command, request);
        // WHEN the exact request is retried THEN its committed result survives the newer coordination version.
        Assert.Equal(posted.ToJsonString(), (await context.CorrectAsync("Credit", command, request)).ToJsonString());
        await context.Bills.AdminAsync("DELETE [Identity].RoleClaims WHERE ClaimValue='SupplierCreditsCorrect'");
        // WHEN correction authority is revoked THEN replay must reauthorize rather than use a stored role snapshot.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Credit", command, request))).Number);
        Assert.Equal(0m, await context.BalanceAsync(original));
    }

    [Fact]
    public async Task ForgedCompositionCannotReleaseCashSettlement()
    {
        // GIVEN a real credit/refund settlement and a trusted test caller with invented parent identity.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var source = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
        var original = Guid.Parse(source["sourceId"]!.GetValue<string>());
        var refund = await context.RefundAsync([(original, "18")]);
        var settlement = Guid.Parse(refund["settlementIds"]![0]!.GetValue<string>());
        var cash = await context.CashEvidenceAsync();
        await context.Bills.AdminAsync("""
            CREATE PROCEDURE Purchasing.AttemptForgedCashRelease
              @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
            AS BEGIN
              SET XACT_ABORT ON;
              BEGIN TRY
                BEGIN TRAN;
                DECLARE @tenant uniqueidentifier=CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),
                  @resource nvarchar(255),@settlement uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.settlementId'));
                SET @resource=N'Accounting:'+CONVERT(nvarchar(36),@tenant);
                EXEC sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction';
                EXEC Purchasing.ReleaseSupplierCashSettlement @ActorId,@SessionId,@RequestId,@settlement,'2026-09-20';
                COMMIT;
              END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
            END;
            """);
        await context.Bills.AdminAsync("GRANT EXECUTE ON Purchasing.AttemptForgedCashRelease TO workbench_web");
        // WHEN no authentic pending correction owns that exact child THEN release authority is unavailable.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("AttemptForgedCashRelease", Guid.NewGuid(),
            new JsonObject { ["settlementId"] = settlement.ToString() }))).Number);
        Assert.Equal(cash, await context.CashEvidenceAsync()); Assert.Equal(0m, await context.BalanceAsync(original));
    }
}
