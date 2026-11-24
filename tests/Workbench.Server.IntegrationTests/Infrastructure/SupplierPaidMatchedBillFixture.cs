// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SupplierPaidMatchedBillFixture(SqlServerFixture server) : IAsyncLifetime
{
    private SqlServerFixture.SqlDatabaseTemplate _database = null!;
    private SupplierContextState _state = null!;
    private Guid _bill;
    private Guid _payment;

    public async Task InitializeAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        await context.EnableBillCorrectionsAsync();
        _bill = await context.ExpenseBillAsync("100");
        var payment = await context.Payments.CommandAsync("100");
        await context.Payments.AllocateAsync(payment, _bill, "100");
        await context.Payments.RecordAsync(payment);
        _payment = Guid.Parse(payment["paymentId"]!.ToString());

        var recognition = context.Bills.Recognition;
        _state = new SupplierContextState(context.Bills.Journal.ConfigurationVersion, recognition.PurchaseOrderId, recognition.SupplierId,
            recognition.PurchaseOrderVersion, JsonSerializer.Serialize(recognition.Accounts), context.Bank, context.Payments.Advance);
        var principal = new SqlConnectionStringBuilder(context.Bills.Journal.Application.WebConnectionString).UserID;
        await context.Bills.Journal.Connection.CloseAsync();
        await context.Bills.AdminAsync($"DELETE FROM [Identity].Sessions; DROP USER [{principal.Replace("]", "]]", StringComparison.Ordinal)}];");
        _database = await server.CaptureTemplateAsync(context.Bills.Journal.Application.AdminConnectionString);
        Console.WriteLine($"Prepared genuine paid matched bill in {clock.Elapsed.TotalSeconds:F3}s; included in gate wall time.");
    }

    internal async Task<(SupplierCreditTestContext Context, Guid Bill, Guid Payment)> OpenAsync()
    {
        var database = await server.RestoreTemplateAsync(_database);
        var paymentContext = await SupplierPaymentTestContext.RestoreAsync(database, _state);
        return (await SupplierCreditTestContext.RestoreAsync(paymentContext), _bill, _payment);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
