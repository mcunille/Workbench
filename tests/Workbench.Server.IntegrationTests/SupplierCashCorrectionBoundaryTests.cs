// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCashCorrectionBoundaryTests(SqlServerFixture sqlServer, SupplierCashClearingFixture clearing) : IClassFixture<SupplierCashClearingFixture>
{
    [Fact]
    public async Task CashCorrectionUsesHistoricalInverseAndTheActualNewFundingAccount()
    {
        // GIVEN a real incoming refund and subsequently archived/renamed historical funding and receivable accounts.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableCashCorrectionsAsync();
        var credit = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18"); var item = Guid.Parse(credit["sourceId"]!.ToString());
        var refund = await context.RefundAsync([(item, "18")]); var source = Guid.Parse(refund["sourceId"]!.ToString());
        var created = await context.Bills.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts", """
            [{"code":"NEWBANK6","name":"Actual replacement receipt bank","type":"Asset","purpose":"Bank"}]
            """);
        var bank = Guid.Parse(JsonNode.Parse(created.Ids)![0]!.ToString());
        var version = await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{bank}'");
        await context.Bills.AdminAsync($"UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME(),Version=NEWID(),Name='Changed after original cash' WHERE Id IN('{context.Bank}','{context.CreditAccount}')");
        var replacement = await context.RefundCommandAsync([(item, "10")], "2026-09-22");
        replacement["fundingAccountId"] = bank.ToString(); replacement["expectedFundingAccountVersion"] = version.ToString();
        var command = await context.CorrectionCommandAsync("Refund", source, replacement, "2026-09-22");
        // WHEN a correction repairs the record and the actual successor receipt uses a different active bank.
        await context.CaptureCashCorrectionPlanAsync("Refund", command); await context.CorrectAsync("Refund", command);
        // THEN the old bank clears by exact historical inversion while the real new bank receives ten.
        Assert.Equal(0m, await context.BankBalanceAsync());
        Assert.Equal(10m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{bank}'"));
        Assert.Equal(8m, await context.BalanceAsync(item));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("""
            SELECT COUNT(*) FROM Accounting.CorrectionGroups correction
            JOIN Accounting.JournalLines original ON original.JournalId=correction.OriginalJournalId
            JOIN Accounting.JournalLines inverse ON inverse.JournalId=correction.ReversalJournalId AND inverse.Ordinal=original.Ordinal
            WHERE original.AccountId<>inverse.AccountId OR original.AccountVersion<>inverse.AccountVersion
              OR original.AccountCode<>inverse.AccountCode OR original.AccountName<>inverse.AccountName
              OR original.AccountType<>inverse.AccountType OR original.AccountPurpose<>inverse.AccountPurpose
              OR original.Debit<>inverse.Credit OR original.Credit<>inverse.Debit
            """));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task ClosedOriginalCashPeriodCanBeCorrectedOnAnOpenDate()
    {
        // GIVEN an authentic refund whose original posting month has been closed.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableCashCorrectionsAsync();
        var credit = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18"); var item = Guid.Parse(credit["sourceId"]!.ToString());
        var refund = await context.RefundAsync([(item, "18")]); var source = Guid.Parse(refund["sourceId"]!.ToString());
        var evidence = await context.CashSourceEvidenceAsync(source);
        await context.Bills.AdminAsync(JournalControlAdapterSql.Install);
        await using (var close = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,@ExpectedConfigurationVersion=@version,@PeriodStart='2026-09-01',@Reason=N'Reconciled',@Evidence=NULL", context.Bills.Journal.Connection))
        {
            close.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); close.Parameters.AddWithValue("@session", context.Bills.Journal.SessionId);
            close.Parameters.AddWithValue("@request", Guid.NewGuid()); close.Parameters.AddWithValue("@version", context.Bills.Journal.ConfigurationVersion);
            await close.ExecuteNonQueryAsync();
        }
        var replacement = await context.RefundCommandAsync([(item, "10")], "2026-10-01");
        var command = await context.CorrectionCommandAsync("Refund", source, replacement, "2026-10-01");
        // WHEN the full correction uses an open posting date THEN retained September source bytes remain unchanged.
        await context.CaptureCashCorrectionPlanAsync("Refund", command); await context.CorrectAsync("Refund", command);
        Assert.Equal(10m, await context.BankBalanceAsync()); Assert.Equal(8m, await context.BalanceAsync(item));
        Assert.Equal(evidence, await context.CashSourceEvidenceAsync(source));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections WHERE PostingDate<'2026-10-01'"));
    }

    [Fact]
    public async Task BackdatedRepaymentCannotSpendClearingAlreadyConsumedAtALaterBoundary()
    {
        // GIVEN genuine clearing of eight already repaid on a later posting date.
        var prepared = await clearing.OpenAsync(); await using var context = prepared.Context; var history = prepared.History;
        await context.RepaymentAsync([(history.Clearing, "8")], "2026-09-23");
        var command = await context.RepaymentCommandAsync([(history.Clearing, "1")], "2026-09-21");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN a backdated record appears affordable on its own date THEN its negative later boundary still rejects atomically.
        await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("RecordSupplierRefundRepayment", Guid.NewGuid(), command));
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        Assert.Equal(0m, await context.BalanceAsync(history.Clearing)); Assert.Equal(10m, await context.BankBalanceAsync());
    }
}
