// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SupplierBillCreditProjectionFixture(SqlServerFixture server) : IAsyncLifetime
{
    private SqlServerFixture.SqlDatabaseTemplate _database = null!;
    private SupplierContextState _state = null!;
    private BillCreditProposal _proposal = null!;

    public async Task InitializeAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        _proposal = await context.PrepareBillAndCreditSuccessorsAsync();
        var recognition = context.Bills.Recognition;
        _state = new SupplierContextState(context.Bills.Journal.ConfigurationVersion, recognition.PurchaseOrderId, recognition.SupplierId,
            recognition.PurchaseOrderVersion, JsonSerializer.Serialize(recognition.Accounts), context.Bank, context.Payments.Advance);
        var principal = new SqlConnectionStringBuilder(context.Bills.Journal.Application.WebConnectionString).UserID;
        await context.Bills.Journal.Connection.CloseAsync();
        await context.Bills.AdminAsync($"DELETE FROM [Identity].Sessions; DROP USER [{principal.Replace("]", "]]", StringComparison.Ordinal)}];");
        _database = await server.CaptureTemplateAsync(context.Bills.Journal.Application.AdminConnectionString);
        Console.WriteLine($"Prepared genuine reviewed bill and credit successors in {clock.Elapsed.TotalSeconds:F3}s; included in gate wall time.");
    }

    internal async Task<(SupplierCreditTestContext Context, BillCreditProposal Proposal)> OpenAsync()
    {
        var database = await server.RestoreTemplateAsync(_database);
        var paymentContext = await SupplierPaymentTestContext.RestoreAsync(database, _state);
        return (await SupplierCreditTestContext.RestoreAsync(paymentContext), _proposal with { Command = _proposal.Command.DeepClone().AsObject() });
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
