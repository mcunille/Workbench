// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierAllocationHistoryTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackdatedAllocationChecksLaterDebtAndFunding(bool debtSide)
    {
        // GIVEN two sources and a real later allocation consuming the shared funding or shared debt.
        await using var context = await SupplierAllocationTestContext.OpenAsync(sqlServer);
        var advance = await context.SourceAsync(); var bill = await context.BillAsync("100");
        var otherAdvance = debtSide ? await context.SourceAsync() : advance;
        var otherBill = debtSide ? bill : await context.BillAsync("100");
        await context.ApplyAsync(await context.CommandAsync(otherAdvance, otherBill, date: "2026-09-18"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
        // AND an already recorded later source release restores the current balance.
        // This disposable historical-state seed does not claim to implement a reversal command.
        await context.SeedLaterReleaseAsync();
        var before = await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierItemMovements");
        Assert.Equal(100m, await context.BalanceAsync(advance)); Assert.Equal(100m, await context.BalanceAsync(bill));
        // WHEN another 100 is backdated to September 16 THEN the intervening oversettlement rejects the whole command.
        var error = await Assert.ThrowsAsync<SqlException>(() => Apply());
        Assert.Equal(51009, error.Number);
        Assert.Equal(before, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierItemMovements"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts"));
        async Task Apply() => await context.ApplyAsync(await context.CommandAsync(advance, bill));
    }

    [Fact]
    public async Task AtomicBoundariesUseExactWideTotalsBeforeStorageBounds()
    {
        // GIVEN one atomic group whose temporary effects exceed storage range but whose final amount fits.
        await using var context = await SupplierAllocationTestContext.OpenAsync(sqlServer);
        var item = Guid.NewGuid(); var group = Guid.NewGuid();
        const string maximum = "999999999999999999999999.9999";
        string Event(string amount) => $$"""{"itemId":"{{item}}","groupId":"{{group}}","postingDate":"2026-09-16","amount":"{{amount}}"}""";
        async Task Check(params string[] amounts)
        {
            var json = "[" + string.Join(',', amounts.Select(Event)) + "]";
            await context.Bills.AdminAsync($"""
                BEGIN TRY
                  BEGIN TRAN;
                  DECLARE @Tenant uniqueidentifier='{JournalTestContext.TenantId}',@Resource nvarchar(255);
                  SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
                  EXEC sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction';
                  EXEC Purchasing.AssertSupplierAvailability @Tenant,N'{json}';
                  ROLLBACK;
                END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
                """);
        }
        // WHEN the complete group is validated THEN only the final exact boundary is constrained.
        await Check("-100", "100");
        await Check(maximum, maximum, "-" + maximum);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => Check(maximum, maximum))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => Check("-0.0001"))).Number);
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => Check("0.00001"))).Number);
    }
}
