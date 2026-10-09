// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

// Shared, immutable histories are created through production commands. Every case
// restores its own database and creates fresh authentication before exercising it.
public abstract class SupplierScenarioFixture(SqlServerFixture sqlServer, params string[] names) : IAsyncLifetime
{
    internal sealed record Scenario(SqlServerFixture.SqlDatabaseTemplate Database, SupplierContextState Context, string DataJson);
    private readonly Dictionary<string, Scenario> _snapshots = new();

    public async Task InitializeAsync()
    {
        foreach (var name in names) _snapshots.Add(name, await sqlServer.GetSupplierScenarioAsync(name));
    }
    public Task DisposeAsync() => Task.CompletedTask; // The collection owns the disposable SQL container.

    internal async Task<PreparedSupplierScenario> OpenAsync(string name)
    {
        var snapshot = _snapshots[name];
        var data = JsonNode.Parse(snapshot.DataJson)!.AsObject();
        var database = await sqlServer.RestoreTemplateAsync(snapshot.Database);
        // RestoreAsync takes ownership, including cleanup if reconstruction fails.
        var context = await SupplierPaymentTestContext.RestoreAsync(database, snapshot.Context);
        return new(context, data);
    }

    internal static async Task<Scenario> CreateAsync(SqlServerFixture server, string? name, Scenario? preparedBase = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var context = preparedBase is null
            ? await SupplierCorrectionFixture.OpenAsync(server)
            : await SupplierPaymentTestContext.RestoreAsync(await server.RestoreTemplateAsync(preparedBase.Database), preparedBase.Context);
        var data = name is null ? new JsonObject() : await SeedAsync(context, name);
        var recognition = context.Bills.Recognition;
        var state = new SupplierContextState(context.Allocation.Journal.ConfigurationVersion,
            recognition.PurchaseOrderId, recognition.SupplierId, recognition.PurchaseOrderVersion,
            JsonSerializer.Serialize(recognition.Accounts), context.Bank, context.Advance);
        // Do not retain a usable contained credential or live session in the backup.
        var principal = new SqlConnectionStringBuilder(context.Allocation.Journal.Application.WebConnectionString).UserID;
        await context.Allocation.Journal.Connection.CloseAsync();
        await context.Bills.AdminAsync($"DELETE FROM [Identity].Sessions; DROP USER [{principal.Replace("]", "]]")}];");
        var database = await server.CaptureTemplateAsync(context.Allocation.Journal.Application.AdminConnectionString);
        var label = name is null ? "supplier base" : $"supplier history '{name}'";
        Console.WriteLine($"Prepared {label} in {clock.Elapsed.TotalSeconds:F3}s; startup remains included in process and gate wall time.");
        return new(database, state, data.ToJsonString());
    }

    private static async Task<JsonObject> SeedAsync(SupplierPaymentTestContext context, string name)
    {
        var data = new JsonObject();
        if (name is "mixed" or "compensation")
        {
            var bill = await context.Allocation.BillAsync("150");
            var payment = await context.CommandAsync("120"); await context.AllocateAsync(payment, bill, "60");
            var posted = await context.RecordAsync(payment); var id = Guid.Parse(payment["paymentId"]!.ToString());
            var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(id, bill, name == "mixed" ? "30" : "60"));
            var embedded = Guid.Parse(posted["applicationIds"]![0]!.ToString());
            var standalone = Guid.Parse(applied["applicationIds"]![0]!.ToString());
            await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), await SupplierCorrectionFixture.ReverseAsync(context, embedded));
            if (name == "compensation")
                await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), await SupplierCorrectionFixture.ReverseAsync(context, standalone));
            Assert.True((await SupplierReconciliationTests.Read(context)).IsComplete);
            var before = await context.Bills.ScalarAsync<DateTimeOffset>("SELECT SYSDATETIMEOFFSET()");
            JsonObject? replacement = null;
            if (name == "mixed")
            {
                replacement = await context.CommandAsync("80", "2026-09-20"); await context.AllocateAsync(replacement, bill, "50");
            }
            await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), await SupplierCorrectionFixture.CorrectionAsync(context, id, replacement));
            if (name == "mixed") data["before"] = before.ToUniversalTime().ToString("O");
            else { data["embedded"] = embedded.ToString(); data["standalone"] = standalone.ToString(); }
        }
        else if (name == "equalDebtAttribution")
        {
            var first = await context.Allocation.BillAsync("50");
            var payable = context.Bills.Recognition.Accounts["SupplierPayable"];
            var version = await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{payable}'");
            var code = await context.Bills.ScalarAsync<string>($"SELECT Code FROM Accounting.Accounts WHERE Id='{payable}'");
            await context.Allocation.Journal.SaveAsync(Guid.NewGuid(), "UpdateAccount",
                new JsonObject { ["code"] = code, ["name"] = "Renamed payable", ["description"] = "Current label" }.ToJsonString(), payable, version);
            var second = await context.Allocation.BillAsync("50");
            var payment = await context.CommandAsync();
            await context.AllocateAsync(payment, first, "50"); await context.AllocateAsync(payment, second, "50");
            var posted = await context.RecordAsync(payment);
            data["first"] = first.ToString(); data["second"] = second.ToString();
            data["payment"] = payment["paymentId"]!.DeepClone(); data["group"] = posted["groupId"]!.DeepClone();
        }
        else if (name == "equalLines")
        {
            var first = await context.Allocation.BillAsync("60");
            await context.Bills.AdminAsync("UPDATE Accounting.Accounts SET Version=NEWID(),Name='Later payable snapshot' WHERE Purpose='SupplierPayable'");
            var second = await context.Allocation.BillAsync("60");
            var payment = await context.CommandAsync("120"); await context.AllocateAsync(payment, first, "60"); await context.AllocateAsync(payment, second, "60");
            await context.RecordAsync(payment);
            await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), await SupplierCorrectionFixture.CorrectionAsync(context, Guid.Parse(payment["paymentId"]!.ToString())));
            data["first"] = first.ToString(); data["second"] = second.ToString();
        }
        else if (name.StartsWith("sources", StringComparison.Ordinal))
        {
            var bill = await context.Allocation.BillAsync("100");
            var payment = await context.CommandAsync(); await context.RecordAsync(payment);
            var funding = Guid.Parse(payment["paymentId"]!.ToString());
            var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill, "60"));
            data["application"] = applied["applicationIds"]![0]!.DeepClone();
            var reverse = await SupplierCorrectionFixture.ReverseAsync(context, Guid.Parse(applied["applicationIds"]![0]!.ToString()));
            if (name == "sourcesTrue")
            {
                var retain = await context.Allocation.CommandAsync(funding, bill, "40", "2026-09-20");
                reverse["reapplications"] = new JsonArray(new JsonObject
                {
                    ["fundingItemId"] = funding.ToString(),
                    ["expectedFundingItemVersion"] = retain["expectedFundingItemVersion"]!.DeepClone(),
                    ["billId"] = bill.ToString(),
                    ["itemId"] = bill.ToString(),
                    ["expectedItemVersion"] = retain["targets"]![0]!["expectedItemVersion"]!.DeepClone(),
                    ["amount"] = "40"
                });
            }
            await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), reverse);
        }
        else if (name == "coordination")
        {
            var bill = await context.Allocation.BillAsync("100"); var payment = await context.CommandAsync(); await context.RecordAsync(payment);
            var funding = Guid.Parse(payment["paymentId"]!.ToString());
            var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill));
            var (document, _) = await context.Bills.SeedDocumentAsync();
            var revision = await context.Bills.ScalarAsync<Guid>($"SELECT RevisionId FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document}'");
            var replacement = await context.CommandAsync("100", "2026-09-20"); await context.AllocateAsync(replacement, bill, "100");
            replacement["evidence"] = new JsonObject { ["documents"] = new JsonArray(new JsonObject { ["documentId"] = document.ToString(), ["revisionId"] = revision.ToString() }), ["missingEvidenceReason"] = null };
            data["bill"] = bill.ToString(); data["funding"] = funding.ToString(); data["application"] = applied["applicationIds"]![0]!.DeepClone();
            data["document"] = document.ToString(); data["replacement"] = replacement.DeepClone();
            data["correction"] = await SupplierCorrectionFixture.CorrectionAsync(context, funding, replacement);
        }
        else if (name == "closure")
        {
            var bill = await context.Allocation.BillAsync("1");
            var payment = await context.CommandAsync("1"); await context.RecordAsync(payment);
            var funding = Guid.Parse(payment["paymentId"]!.ToString());
            var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill, "1", "2026-09-20"));
            var application = Guid.Parse(applied["applicationIds"]![0]!.ToString());
            await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), await SupplierCorrectionFixture.ReverseAsync(context, application));
            data["application"] = application.ToString(); data["command"] = await SupplierCorrectionFixture.CorrectionAsync(context, funding);
        }
        else if (name == "restoredDebt")
        {
            var first = await context.Allocation.BillAsync("100"); var second = await context.Allocation.BillAsync("100");
            var payment = await context.CommandAsync(); await context.AllocateAsync(payment, first, "100");
            var posted = await context.RecordAsync(payment); var id = Guid.Parse(payment["paymentId"]!.ToString());
            await context.Bills.ExecuteAsync("ReverseSupplierApplication", Guid.NewGuid(), await SupplierCorrectionFixture.ReverseAsync(context, Guid.Parse(posted["applicationIds"]![0]!.ToString())));
            var laterPayment = await context.CommandAsync("100", "2026-09-20"); await context.AllocateAsync(laterPayment, first, "100"); await context.RecordAsync(laterPayment);
            await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(id, second, "40", "2026-09-20"));
            data["first"] = first.ToString(); data["second"] = second.ToString(); data["id"] = id.ToString(); data["laterPayment"] = laterPayment;
            data["command"] = await SupplierCorrectionFixture.CorrectionAsync(context, id);
        }
        else if (name == "recovery")
        {
            var bill = await context.Allocation.BillAsync("150"); var (document, revision) = await context.Bills.SeedDocumentAsync();
            var payment = await context.CommandAsync("100", "2026-09-10");
            payment["evidence"] = new JsonObject { ["documents"] = new JsonArray(new JsonObject { ["documentId"] = document.ToString(), ["revisionId"] = revision.ToString() }), ["missingEvidenceReason"] = null };
            var request = Guid.NewGuid(); var receipt = await context.RecordAsync(payment, request);
            var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(Guid.Parse(payment["paymentId"]!.ToString()), bill, "60"));
            var reverse = await SupplierCorrectionFixture.ReverseAsync(context, Guid.Parse(applied["applicationIds"]![0]!.ToString()));
            var reversalRequest = Guid.NewGuid(); var reversed = await context.Bills.ExecuteAsync("ReverseSupplierApplication", reversalRequest, reverse);
            data["document"] = document.ToString(); data["revision"] = revision.ToString(); data["payment"] = payment;
            data["request"] = request.ToString(); data["receipt"] = receipt; data["reverse"] = reverse;
            data["reversalRequest"] = reversalRequest.ToString(); data["reversed"] = reversed;
        }
        else if (name.StartsWith("receipt", StringComparison.Ordinal))
        {
            var bill = await context.Allocation.BillAsync("100"); var payment = await context.CommandAsync(); await context.AllocateAsync(payment, bill, "100");
            var posted = await context.RecordAsync(payment); var id = Guid.Parse(payment["paymentId"]!.ToString());
            JsonObject input;
            if (name == "receiptTrue")
            {
                input = await SupplierCorrectionFixture.ReverseAsync(context, Guid.Parse(posted["applicationIds"]![0]!.ToString()));
                input["reapplications"] = new JsonArray(new JsonObject
                {
                    ["fundingItemId"] = id.ToString(),
                    ["expectedFundingItemVersion"] = await context.Allocation.VersionAsync(id),
                    ["billId"] = bill.ToString(),
                    ["itemId"] = bill.ToString(),
                    ["expectedItemVersion"] = await context.Allocation.VersionAsync(bill),
                    ["amount"] = "40"
                });
            }
            else
            {
                var replacement = await context.CommandAsync("40", "2026-09-20"); await context.AllocateAsync(replacement, bill, "40");
                input = await SupplierCorrectionFixture.CorrectionAsync(context, id, replacement);
            }
            data["input"] = input;
        }
        else throw new ArgumentOutOfRangeException(nameof(name));
        return data;
    }
}

public sealed class SupplierCorrectionScenarios(SqlServerFixture server) : SupplierScenarioFixture(server,
    "coordination", "closure", "restoredDebt", "receiptFalse", "receiptTrue");

public sealed class SupplierReconciliationScenarios(SqlServerFixture server) : SupplierScenarioFixture(server,
    "mixed", "compensation", "equalLines", "sourcesFalse", "sourcesTrue");

public sealed class SupplierRecoveryScenarios(SqlServerFixture server) : SupplierScenarioFixture(server, "recovery");

public sealed class SupplierIsolationScenarios(SqlServerFixture server) : SupplierScenarioFixture(server, "sourcesFalse");

public sealed class SupplierEvidenceScenarios(SqlServerFixture server) : SupplierScenarioFixture(server, "equalDebtAttribution");

internal sealed record SupplierContextState(Guid ConfigurationVersion, Guid PurchaseOrderId, Guid SupplierId,
    string PurchaseOrderVersion, string AccountsJson, Guid Bank, Guid Advance);

internal sealed record PreparedSupplierScenario(SupplierPaymentTestContext Context, JsonObject Data) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Context.DisposeAsync();
}
