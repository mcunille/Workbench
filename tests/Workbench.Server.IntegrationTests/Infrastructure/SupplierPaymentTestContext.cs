// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed class SupplierPaymentTestContext(SupplierAllocationTestContext allocation) : IAsyncDisposable
{
    public SupplierAllocationTestContext Allocation { get; } = allocation;
    public SupplierBillTestContext Bills => Allocation.Bills;
    public Guid Bank { get; private set; }
    public Guid Advance { get; private set; }

    internal static async Task<SupplierPaymentTestContext> RestoreAsync(SqlTestDatabase database, SupplierContextState state)
    {
        var application = await AuthTestApplication.CreateFromDatabaseAsync(database);
        JournalTestContext? journal = null;
        try
        {
            journal = await JournalTestContext.OpenRestoredAsync(application, state.ConfigurationVersion);
            var recognition = PurchaseRecognitionTestContext.Restore(journal, state);
            var allocation = new SupplierAllocationTestContext(new SupplierOpenItemTestContext(new SupplierBillTestContext(recognition)));
            return new SupplierPaymentTestContext(allocation) { Bank = state.Bank, Advance = state.Advance };
        }
        catch
        {
            if (journal is not null) await journal.Connection.DisposeAsync();
            await application.DisposeAsync();
            throw;
        }
    }
    public static async Task<SupplierPaymentTestContext> OpenAsync(SqlServerFixture fixture, string? priorMigration = null)
    {
        var result = new SupplierPaymentTestContext(await SupplierAllocationTestContext.OpenAsync(fixture, priorMigration));
        try
        {
            var created = await result.Allocation.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts", """
                [{"code":"BANK4","name":"Payment bank","type":"Asset","purpose":"Bank"},
                 {"code":"ADV4","name":"Supplier advances","type":"Asset","purpose":"SupplierAdvance"}]
                """);
            var ids = JsonNode.Parse(created.Ids)!.AsArray();
            result.Bank = Guid.Parse(ids[0]!.GetValue<string>()); result.Advance = Guid.Parse(ids[1]!.GetValue<string>());
            await result.Bills.AdminAsync($$"""
                UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,'append $.mappings',JSON_QUERY('{"slot":"SupplierAdvance","accountId":"{{result.Advance}}"}'));
                GRANT EXECUTE ON Purchasing.RecordSupplierPayment TO workbench_web;
                INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
                  SELECT TenantId,RoleId,N'workbench/permission',N'SupplierPaymentsRecord'
                  FROM Administration.AccountingRoles WHERE Kind='Administrator';
                """);
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }
    public async Task<JsonObject> CommandAsync(string amount = "100", string date = "2026-09-16") => new()
    {
        ["schemaVersion"] = 1,
        ["operation"] = "RecordSupplierPayment",
        ["expectedConfigurationVersion"] = Allocation.Journal.ConfigurationVersion.ToString(),
        ["purchaseOrderId"] = Bills.Recognition.PurchaseOrderId.ToString(),
        ["expectedPurchaseOrderVersion"] = Bills.Recognition.PurchaseOrderVersion,
        ["supplierId"] = Bills.Recognition.SupplierId.ToString(),
        ["currency"] = "USD",
        ["postingDate"] = date,
        ["paymentId"] = Guid.NewGuid().ToString(),
        ["paymentRevisionId"] = Guid.NewGuid().ToString(),
        ["paymentDate"] = date,
        ["effectiveDate"] = date,
        ["amount"] = amount,
        ["method"] = "Bank",
        ["fundingAccountId"] = Bank.ToString(),
        ["expectedFundingAccountVersion"] = (await Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{Bank}'")).ToString(),
        ["reference"] = "Transfer 42",
        ["notes"] = "Supplier payment",
        ["evidence"] = new JsonObject { ["documents"] = new JsonArray(), ["missingEvidenceReason"] = "Receipt not supplied" },
        ["allocations"] = new JsonArray()
    };
    public async Task AllocateAsync(JsonObject command, Guid bill, string amount)
        => command["allocations"]!.AsArray().Add(new JsonObject
        {
            ["billId"] = bill.ToString(),
            ["itemId"] = bill.ToString(),
            ["expectedItemVersion"] = await Allocation.VersionAsync(bill),
            ["amount"] = amount
        });
    public Task<JsonObject> RecordAsync(JsonObject command, Guid? request = null)
        => Bills.ExecuteAsync("RecordSupplierPayment", request ?? Guid.NewGuid(), command);
    public ValueTask DisposeAsync() => Allocation.DisposeAsync();
}
