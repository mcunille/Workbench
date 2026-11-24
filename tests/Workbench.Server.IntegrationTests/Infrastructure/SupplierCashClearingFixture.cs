// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed record SupplierCashClearingHistory(Guid Bill, Guid OriginalCredit, Guid SuccessorCredit, Guid Refund, Guid Clearing);

public class SupplierCashClearingFixture(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private SqlServerFixture.SqlDatabaseTemplate? _database;
    private SupplierContextState? _state;
    private SupplierCashClearingHistory? _history;

    public async Task InitializeAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableCashCorrectionsAsync();
        _history = await PrepareAsync(context);
        var recognition = context.Bills.Recognition;
        _state = new SupplierContextState(context.Bills.Journal.ConfigurationVersion,
            recognition.PurchaseOrderId, recognition.SupplierId, recognition.PurchaseOrderVersion,
            JsonSerializer.Serialize(recognition.Accounts), context.Bank, context.Payments.Advance);
        var principal = new SqlConnectionStringBuilder(context.Bills.Journal.Application.WebConnectionString).UserID;
        await context.Bills.Journal.Connection.CloseAsync();
        await context.Bills.AdminAsync($"DELETE FROM [Identity].Sessions; DROP USER [{principal.Replace("]", "]]")}];");
        _database = await sqlServer.CaptureTemplateAsync(context.Bills.Journal.Application.AdminConnectionString);
        Console.WriteLine($"Prepared genuine refund-clearing history in {clock.Elapsed.TotalSeconds:F3}s; included in process and gate wall time.");
    }

    internal async Task<(SupplierCreditTestContext Context, SupplierCashClearingHistory History)> OpenAsync()
    {
        var database = await sqlServer.RestoreTemplateAsync(_database ?? throw new InvalidOperationException("History is not prepared."));
        var payments = await SupplierPaymentTestContext.RestoreAsync(database, _state ?? throw new InvalidOperationException("History is not prepared."));
        return (await SupplierCreditTestContext.RestoreAsync(payments), _history ?? throw new InvalidOperationException("History is not prepared."));
    }

    internal virtual async Task<SupplierCashClearingHistory> PrepareAsync(SupplierCreditTestContext context)
    {
        var history = await context.PrepareRefundClearingAsync();
        return new(history.Bill, history.OriginalCredit, history.SuccessorCredit, history.Refund, history.Clearing);
    }

    public virtual Task DisposeAsync() => Task.CompletedTask; // The collection owns the disposable SQL container and backups.
}
