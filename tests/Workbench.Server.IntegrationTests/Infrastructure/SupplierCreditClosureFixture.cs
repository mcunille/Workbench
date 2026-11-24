// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SupplierCreditClosureFixture(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private SqlServerFixture.SqlDatabaseTemplate? _database;
    private SupplierContextState? _state;
    internal Guid CreditId { get; private set; }

    public async Task InitializeAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var source = await context.CreditAsync(await context.ExpenseBillAsync("100"), "20");
        CreditId = Guid.Parse(source["sourceId"]!.GetValue<string>());
        var refund = await context.RefundCommandAsync([(CreditId, "0.01")]);
        // 334 actual receipts produce over 1,000 discovered dependency/effect events.
        // The asserted correction transition remains inside the timed case.
        await using (var batch = new SqlCommand("""
            DECLARE @index int=0,@request uniqueidentifier,@command nvarchar(max),@version varchar(18);
            WHILE @index<334
            BEGIN
              SELECT @version=CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierItemVersions WHERE ItemId=@item;
              SET @command=JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(@template,'$.cashSourceId',CONVERT(nvarchar(36),NEWID())),
                '$.cashSourceRevisionId',CONVERT(nvarchar(36),NEWID())),'$.settlements[0].expectedItemVersion',@version);
              SET @request=NEWID();
              EXEC Purchasing.RecordSupplierRefund @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Command=@command;
              SET @index+=1;
            END;
            """, context.Bills.Journal.Connection) { CommandTimeout = 180 })
        {
            batch.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
            batch.Parameters.AddWithValue("@session", context.Bills.Journal.SessionId);
            batch.Parameters.AddWithValue("@item", CreditId);
            batch.Parameters.AddWithValue("@template", refund.ToJsonString());
            await batch.ExecuteNonQueryAsync();
        }
        var recognition = context.Bills.Recognition;
        _state = new SupplierContextState(context.Bills.Journal.ConfigurationVersion,
            recognition.PurchaseOrderId, recognition.SupplierId, recognition.PurchaseOrderVersion,
            JsonSerializer.Serialize(recognition.Accounts), context.Bank, context.Payments.Advance);
        var principal = new SqlConnectionStringBuilder(context.Bills.Journal.Application.WebConnectionString).UserID;
        await context.Bills.Journal.Connection.CloseAsync();
        await context.Bills.AdminAsync($"DELETE FROM [Identity].Sessions; DROP USER [{principal.Replace("]", "]]")}];");
        _database = await sqlServer.CaptureTemplateAsync(context.Bills.Journal.Application.AdminConnectionString);
        Console.WriteLine($"Prepared authentic BK-08 oversized history in {clock.Elapsed.TotalSeconds:F3}s; included in process and gate wall time.");
    }

    internal async Task<SupplierCreditTestContext> OpenAsync()
    {
        var database = await sqlServer.RestoreTemplateAsync(_database ?? throw new InvalidOperationException("History is not prepared."));
        var payments = await SupplierPaymentTestContext.RestoreAsync(database, _state ?? throw new InvalidOperationException("History is not prepared."));
        return await SupplierCreditTestContext.RestoreAsync(payments);
    }

    public Task DisposeAsync() => Task.CompletedTask; // The collection owns SQL/container cleanup.
}
