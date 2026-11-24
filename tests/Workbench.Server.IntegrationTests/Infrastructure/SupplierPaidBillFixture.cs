// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SupplierPaidBillFixture(SqlServerFixture server) : IAsyncLifetime
{
    private sealed record History(SqlServerFixture.SqlDatabaseTemplate Database, SupplierContextState State, Guid Bill, Guid Funding);
    private History _paid = null!;
    private History _standalone = null!;

    public async Task InitializeAsync()
    {
        _paid = await PrepareAsync(embedPayment: true);
        _standalone = await PrepareAsync(embedPayment: false);
    }

    private async Task<History> PrepareAsync(bool embedPayment)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        await context.EnableBillCorrectionsAsync();
        var bill = await context.InvoiceOnlyBillAsync("300");
        var payment = await context.Payments.CommandAsync("300");
        if (embedPayment) await context.Payments.AllocateAsync(payment, bill, "300");
        await context.Payments.RecordAsync(payment);
        var funding = Guid.Parse(payment["paymentId"]!.GetValue<string>());

        var recognition = context.Bills.Recognition;
        var state = new SupplierContextState(context.Bills.Journal.ConfigurationVersion, recognition.PurchaseOrderId,
            recognition.SupplierId, recognition.PurchaseOrderVersion, JsonSerializer.Serialize(recognition.Accounts),
            context.Bank, context.Payments.Advance);
        var principal = new SqlConnectionStringBuilder(context.Bills.Journal.Application.WebConnectionString).UserID;
        await context.Bills.Journal.Connection.CloseAsync();
        await context.Bills.AdminAsync($"DELETE FROM [Identity].Sessions; DROP USER [{principal.Replace("]", "]]", StringComparison.Ordinal)}];");
        var database = await server.CaptureTemplateAsync(context.Bills.Journal.Application.AdminConnectionString);
        Console.WriteLine($"Prepared authentic bill and payment in {clock.Elapsed.TotalSeconds:F3}s; included in gate wall time.");
        return new(database, state, bill, funding);
    }

    internal async Task<(SupplierCreditTestContext Context, Guid Bill, Guid Advance)> OpenAsync(bool embedPayment)
    {
        var history = embedPayment ? _paid : _standalone;
        var payments = await SupplierPaymentTestContext.RestoreAsync(await server.RestoreTemplateAsync(history.Database), history.State);
        return (await SupplierCreditTestContext.RestoreAsync(payments), history.Bill, history.Funding);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
