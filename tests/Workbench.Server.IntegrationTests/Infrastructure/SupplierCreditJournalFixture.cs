// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SupplierCreditJournalFixture(SqlServerFixture server) : IAsyncLifetime
{
    private sealed record History(SqlServerFixture.SqlDatabaseTemplate Database, SupplierContextState State,
        Guid Bill, Guid Credit, Guid? Settlement);

    private History _refunded = null!;
    private History _allocated = null!;

    public async Task InitializeAsync()
    {
        _refunded = await PrepareAsync(refund: true);
        _allocated = await PrepareAsync(refund: false);
    }

    private async Task<History> PrepareAsync(bool refund)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        var bill = await context.ExpenseBillAsync("100");
        Guid? settlement = null;
        Guid credit;
        if (refund)
        {
            var payment = await context.Payments.CommandAsync("100", "2026-09-16");
            await context.Payments.AllocateAsync(payment, bill, "100");
            await context.Payments.RecordAsync(payment);
            credit = Guid.Parse((await context.CreditAsync(bill, "18"))["sourceId"]!.ToString());
            var receipt = await context.RefundAsync([(credit, "18")]);
            settlement = Guid.Parse(receipt["settlementIds"]![0]!.ToString());
        }
        else
        {
            credit = Guid.Parse((await context.CreditAsync(bill, "30"))["sourceId"]!.ToString());
            await context.Payments.Allocation.ApplyAsync(await context.Payments.Allocation.CommandAsync(credit, bill, "20"));
        }

        var recognition = context.Bills.Recognition;
        var state = new SupplierContextState(context.Bills.Journal.ConfigurationVersion, recognition.PurchaseOrderId, recognition.SupplierId,
            recognition.PurchaseOrderVersion, JsonSerializer.Serialize(recognition.Accounts), context.Bank, context.Payments.Advance);
        var principal = new SqlConnectionStringBuilder(context.Bills.Journal.Application.WebConnectionString).UserID;
        await context.Bills.Journal.Connection.CloseAsync();
        await context.Bills.AdminAsync($"DELETE FROM [Identity].Sessions; DROP USER [{principal.Replace("]", "]]", StringComparison.Ordinal)}];");
        var database = await server.CaptureTemplateAsync(context.Bills.Journal.Application.AdminConnectionString);
        Console.WriteLine($"Prepared genuine {(refund ? "refunded" : "allocated")} credit history in {clock.Elapsed.TotalSeconds:F3}s; included in gate wall time.");
        return new(database, state, bill, credit, settlement);
    }

    internal async Task<(SupplierCreditTestContext Context, Guid Bill, Guid Credit, Guid? Settlement)> OpenAsync(bool refund)
    {
        var history = refund ? _refunded : _allocated;
        var database = await server.RestoreTemplateAsync(history.Database);
        var paymentContext = await SupplierPaymentTestContext.RestoreAsync(database, history.State);
        return (await SupplierCreditTestContext.RestoreAsync(paymentContext), history.Bill, history.Credit, history.Settlement);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
