// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SupplierCreditCorrectionHistories(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private sealed record History(SqlServerFixture.SqlDatabaseTemplate Database, SupplierContextState State, string Data);
    private readonly Dictionary<string, History> _histories = new();

    public async Task InitializeAsync()
    {
        foreach (var name in new[] { "partial", "second-generation" })
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
            var firstBill = await context.ExpenseBillAsync("100");
            var secondBill = name == "partial" ? await context.ExpenseBillAsync("100") : Guid.Empty;
            var credit = await context.CreditAsync(firstBill, name == "partial" ? "30" : "18");
            var original = Guid.Parse(credit["sourceId"]!.ToString());
            var refund = await context.RefundAsync([(original, name == "partial" ? "10" : "18")]);
            var debt = Guid.Parse(refund["settlementIds"]![0]!.ToString());
            var data = new JsonObject { ["bill"] = firstBill.ToString(), ["original"] = original.ToString(), ["debt"] = debt.ToString() };
            if (name == "partial")
            {
                data["secondBill"] = secondBill.ToString();
                await context.Payments.Allocation.ApplyAsync(await context.Payments.Allocation.CommandAsync(original, firstBill, "8"));
                await context.Payments.Allocation.ApplyAsync(await context.Payments.Allocation.CommandAsync(original, secondBill, "5"));
            }
            else
            {
                data["cashBeforeFirst"] = await context.CashEvidenceAsync();
                var replacement = await context.CreditCommandAsync(firstBill, "10", "2026-09-20");
                var successor = Guid.Parse(replacement["creditId"]!.ToString()); data["firstId"] = successor.ToString();
                var correction = await context.CorrectionCommandAsync("Credit", original, replacement);
                correction["reapplications"]!.AsArray().Add(new JsonObject
                {
                    ["fundingItemId"] = successor.ToString(),
                    ["debtItemId"] = debt.ToString(),
                    ["amount"] = "10"
                });
                // The second-generation case's GIVEN is already corrected once. Its asserted
                // next correction remains in the timed case; first-generation behavior has its own owner.
                await context.CaptureCorrectionPlanAsync(correction); await context.CorrectAsync("Credit", correction);
            }
            var recognition = context.Bills.Recognition;
            var state = new SupplierContextState(context.Bills.Journal.ConfigurationVersion,
                recognition.PurchaseOrderId, recognition.SupplierId, recognition.PurchaseOrderVersion,
                JsonSerializer.Serialize(recognition.Accounts), context.Bank, context.Payments.Advance);
            var principal = new SqlConnectionStringBuilder(context.Bills.Journal.Application.WebConnectionString).UserID;
            await context.Bills.Journal.Connection.CloseAsync();
            await context.Bills.AdminAsync($"DELETE FROM [Identity].Sessions; DROP USER [{principal.Replace("]", "]]")}];");
            var database = await sqlServer.CaptureTemplateAsync(context.Bills.Journal.Application.AdminConnectionString);
            _histories.Add(name, new(database, state, data.ToJsonString()));
            Console.WriteLine($"Prepared genuine credit correction history '{name}' in {clock.Elapsed.TotalSeconds:F3}s; included in process and gate wall time.");
        }
    }

    internal async Task<(SupplierCreditTestContext Context, JsonObject Data)> OpenAsync(string name)
    {
        var history = _histories[name];
        var database = await sqlServer.RestoreTemplateAsync(history.Database);
        var payments = await SupplierPaymentTestContext.RestoreAsync(database, history.State);
        return (await SupplierCreditTestContext.RestoreAsync(payments), JsonNode.Parse(history.Data)!.AsObject());
    }

    public Task DisposeAsync() => Task.CompletedTask; // The collection owns the disposable SQL container and backups.
}
