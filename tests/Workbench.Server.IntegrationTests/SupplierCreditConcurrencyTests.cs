// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditConcurrencyTests(SqlServerFixture server)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreditAndDocumentRemovalHaveOnlyOneLegalWinner(bool creditFirst)
    {
        // GIVEN actual uploaded evidence and a credit bound to that exact revision.
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var bill = await context.ExpenseBillAsync("100");
        var document = await FinancialEvidencePostingTests.UploadAsync(context.Bills, storage);
        var credit = await context.CreditCommandAsync(bill, "18");
        SupplierCreditTestContext.AddRecoveryEvidence(credit, document);
        var request = Guid.NewGuid();
        var first = context.Bills.Journal.Connection;
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        async Task Remove(SqlConnection connection)
        {
            await using var command = new SqlCommand(FinancialEvidenceRemovalTests.PrepareSql(context.Bills, document.Document, request), connection);
            await command.ExecuteNonQueryAsync();
            command.CommandText = "EXEC Purchasing.FinishPurchaseOrderDocument @request,0;";
            command.Parameters.AddWithValue("@request", request);
            await command.ExecuteNonQueryAsync();
        }
        Task Post(SqlConnection connection) => context.Bills.ExecuteAsync("PostSupplierCredit", Guid.NewGuid(), credit, connection);
        Task<Exception?> losing;
        await FinancialEvidenceRemovalTests.DocumentSqlAsync(context.Bills, "BEGIN TRAN;");
        try
        {
            await (creditFirst ? Post(first) : Remove(first));
            losing = Record.ExceptionAsync(() => creditFirst ? Remove(sibling) : Post(sibling));
            // WHEN SQL proves that the losing request is blocked behind the winning transaction.
            await FinancialEvidenceConcurrencyTests.WaitForBlockedAsync(context.Bills, first, sibling);
        }
        finally { await FinancialEvidenceRemovalTests.DocumentSqlAsync(context.Bills, "IF @@TRANCOUNT>0 COMMIT;"); }
        // THEN the source captures a durable hold or a removed file makes the credit fail atomically.
        var error = Assert.IsType<SqlException>(await losing);
        Assert.Equal(creditFirst ? 51011 : 51009, error.Number);
        Assert.Equal(creditFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
        Assert.Equal(creditFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        if (creditFirst) await FinancialEvidenceRemovalTests.AssertPreservedAsync(context.Bills, storage, document.Revision);
        else Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NOT NULL"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentCreditClaimsCannotOverdrawOnePostedComponent(bool firstWins)
    {
        // GIVEN one hundred dollars of authentic bill component capacity and two sixty-dollar claims.
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        var bill = await context.ExpenseBillAsync("100");
        var first = await context.CreditCommandAsync(bill, "60");
        var second = await context.CreditCommandAsync(bill, "60");
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        // WHEN both serial orders overlap behind the observed SQL lock.
        var error = await InOrder(context, sibling, firstWins,
            () => context.Bills.ExecuteAsync("PostSupplierCredit", Guid.NewGuid(), first),
            () => context.Bills.ExecuteAsync("PostSupplierCredit", Guid.NewGuid(), second, sibling));
        // THEN only the winning claim posts and the original debt and cash are untouched.
        Assert.NotNull(error);
        Assert.Equal(51009, error.Number);
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
        Assert.Equal(Guid.Parse((firstWins ? first : second)["creditId"]!.ToString()),
            await context.Bills.ScalarAsync<Guid>("SELECT Id FROM Purchasing.SupplierCredits"));
        Assert.Equal(60m, await context.Bills.ScalarAsync<decimal>("SELECT SUM(Amount) FROM Purchasing.SupplierCreditClaims"));
        Assert.Equal(60m, await context.Bills.ScalarAsync<decimal>("SELECT SUM(Total) FROM Purchasing.SupplierCredits"));
        Assert.Equal(100m, await context.BalanceAsync(bill));
        Assert.Equal(0m, await context.BankBalanceAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreditPostingAndPeriodCloseRespectBothOrders(bool postingFirst)
    {
        // GIVEN a ready credit and the independent September close command.
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        var bill = await context.ExpenseBillAsync("100");
        var credit = await context.CreditCommandAsync(bill, "18");
        await context.Bills.AdminAsync(JournalControlAdapterSql.Install);
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        async Task Close()
        {
            await using var command = new SqlCommand("""
                EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,
                    @ExpectedConfigurationVersion=@version,@PeriodStart='2026-09-01',@Reason=N'Reconciled',@Evidence=NULL
                """, sibling);
            command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
            command.Parameters.AddWithValue("@session", context.Bills.Journal.SessionId);
            command.Parameters.AddWithValue("@request", Guid.NewGuid());
            command.Parameters.AddWithValue("@version", context.Bills.Journal.ConfigurationVersion);
            await command.ExecuteNonQueryAsync();
        }
        // WHEN the database serializes the operations THEN no prohibited journal can appear after a completed close.
        var error = await InOrder(context, sibling, postingFirst,
            () => context.Bills.ExecuteAsync("PostSupplierCredit", Guid.NewGuid(), credit), Close);
        if (postingFirst) Assert.Null(error);
        else Assert.Equal(51009, error?.Number);
        Assert.Equal(1, await context.Bills.Journal.CountAsync("PeriodClosures"));
        Assert.Equal(postingFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.SourceEvents WHERE SourceKind='SupplierCredit'"));
        Assert.Equal(postingFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts WHERE Operation='PostSupplierCredit'"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BankArchivalAndRefundRespectActualFundingOwnership(bool refundFirst)
    {
        // GIVEN a genuine credit and a refund using an active bank with no earlier posting.
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        var credit = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
        var item = Guid.Parse(credit["sourceId"]!.ToString());
        var refund = await context.RefundCommandAsync([(item, "8")]);
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        // WHEN both operations cross the real accounting lock THEN the archive wins only before cash is posted.
        var error = await InOrder(context, sibling, refundFirst,
            () => context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), refund),
            () => context.Bills.Journal.SaveAsync(Guid.NewGuid(), "ArchiveAccount", """{"isArchived":true}""",
                context.Bank, Guid.Parse(refund["expectedFundingAccountVersion"]!.ToString()), sibling));
        Assert.Equal(refundFirst ? 50909 : 51004, error?.Number);
        Assert.Equal(refundFirst ? 10m : 18m, await context.BalanceAsync(item));
        Assert.Equal(refundFirst ? 8m : 0m, await context.BankBalanceAsync());
        Assert.Equal(refundFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSources"));
    }

    private static Task<SqlException?> InOrder(SupplierCreditTestContext context, SqlConnection sibling, bool firstWins,
        Func<Task> first, Func<Task> second) => PurchaseRecognitionConcurrencyTests.InOrderAsync(context.Bills.Recognition,
            firstWins ? context.Bills.Journal.Connection : sibling, firstWins ? sibling : context.Bills.Journal.Connection,
            firstWins ? first : second, firstWins ? second : first);
}
