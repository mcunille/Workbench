// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed partial class SupplierCreditTestContext(SupplierPaymentTestContext payments) : IAsyncDisposable
{
    public SupplierPaymentTestContext Payments { get; } = payments;
    public SupplierBillTestContext Bills => Payments.Bills;
    public Guid Bank => Payments.Bank;
    public Guid CreditAccount { get; private set; }
    public Guid ClearingAccount { get; private set; }

    public static async Task<SupplierCreditTestContext> OpenAsync(SqlServerFixture fixture)
    {
        var context = new SupplierCreditTestContext(await SupplierPaymentTestContext.OpenAsync(fixture));
        try
        {
            var accounts = await context.Payments.Allocation.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts", """
                [{"code":"CRBK08","name":"Supplier credit receivables","type":"Asset","purpose":"SupplierCreditReceivable"},
                 {"code":"RCBK08","name":"Supplier refund clearing","type":"Liability","purpose":"SupplierRefundClearing"}]
                """);
            var ids = JsonNode.Parse(accounts.Ids)!.AsArray();
            context.CreditAccount = Guid.Parse(ids[0]!.GetValue<string>());
            context.ClearingAccount = Guid.Parse(ids[1]!.GetValue<string>());
            await context.Bills.AdminAsync($$"""
                UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(JSON_MODIFY(Payload,
                  'append $.mappings',JSON_QUERY('{"slot":"SupplierCreditReceivable","accountId":"{{context.CreditAccount}}"}')),
                  'append $.mappings',JSON_QUERY('{"slot":"SupplierRefundClearing","accountId":"{{context.ClearingAccount}}"}'));
                INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
                  SELECT TenantId,RoleId,N'workbench/permission',N'SupplierCreditsPost'
                  FROM Administration.AccountingRoles WHERE Kind='Administrator';
                IF OBJECT_ID(N'Purchasing.PostSupplierCredit') IS NOT NULL
                  GRANT EXECUTE ON Purchasing.PostSupplierCredit TO workbench_web;
                INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
                  SELECT TenantId,RoleId,N'workbench/permission',N'SupplierRefundsRecord'
                  FROM Administration.AccountingRoles WHERE Kind='Administrator';
                IF OBJECT_ID(N'Purchasing.RecordSupplierRefund') IS NOT NULL
                  GRANT EXECUTE ON Purchasing.RecordSupplierRefund TO workbench_web;
                INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
                  SELECT TenantId,RoleId,N'workbench/permission',N'SupplierCreditsCorrect'
                  FROM Administration.AccountingRoles WHERE Kind='Administrator';
                IF OBJECT_ID(N'Purchasing.PreviewSupplierCreditCorrection') IS NOT NULL
                  GRANT EXECUTE ON Purchasing.PreviewSupplierCreditCorrection TO workbench_web;
                IF OBJECT_ID(N'Purchasing.CorrectSupplierCredit') IS NOT NULL
                  GRANT EXECUTE ON Purchasing.CorrectSupplierCredit TO workbench_web;
                """);
            return context;
        }
        catch { await context.DisposeAsync(); throw; }
    }

    public async Task<Guid> ExpenseBillAsync(string amount, string date = "2026-09-15",
        string classification = "Expense", string tax = "0", string rounding = "0")
    {
        var cost = (decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)
            + decimal.Parse(rounding, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var receipt = await Bills.Recognition.CommandAsync(classification: classification, cost: cost);
        receipt["postingDate"] = date;
        await Bills.Recognition.PostAsync(receipt.ToJsonString());
        var draft = Bills.CompleteDraft(Guid.NewGuid().ToString());
        var revision = draft["revision"]!.AsObject();
        revision["total"] = (decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture)
            + decimal.Parse(tax, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        revision["postingDate"] = date;
        var unit = revision["units"]![0]!;
        unit["classification"] = classification;
        unit["unitId"] = receipt["units"]![0]!["unitId"]!.DeepClone();
        unit["expectedPriorEventRevision"] = 1;
        unit["components"]![0]!["amount"] = amount;
        unit["components"]![1]!["amount"] = tax;
        if (rounding != "0") unit["components"]!.AsArray().Add(new JsonObject
        {
            ["componentKey"] = "rounding",
            ["kind"] = "Rounding",
            ["amount"] = rounding,
            ["assignedCostComponentKey"] = "base",
            ["reason"] = "Supplier rounded the original invoice"
        });
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(Bills, draft);
        await Bills.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), Bills.PostCommand(reviewed));
        return Guid.Parse(reviewed["billId"]!.GetValue<string>());
    }

    public async Task<JsonObject> CreditCommandAsync(Guid bill, string amount, string date = "2026-09-16")
    {
        var revision = await Bills.ScalarAsync<Guid>($"SELECT RevisionId FROM Purchasing.SupplierBillPostings WHERE BillId='{bill}'");
        var invoice = await Bills.ScalarAsync<Guid>($"SELECT e.Id FROM Purchasing.SupplierBillPostingEvents b JOIN Purchasing.RecognitionSideEvents e ON e.TenantId=b.TenantId AND e.Id=b.EventId WHERE b.BillId='{bill}' AND e.Side='Invoice'");
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["operation"] = "PostSupplierCredit",
            ["expectedConfigurationVersion"] = Bills.Journal.ConfigurationVersion.ToString(),
            ["purchaseOrderId"] = Bills.Recognition.PurchaseOrderId.ToString(),
            ["expectedPurchaseOrderVersion"] = Bills.Recognition.PurchaseOrderVersion,
            ["supplierId"] = Bills.Recognition.SupplierId.ToString(),
            ["currency"] = "USD",
            ["postingDate"] = date,
            ["creditId"] = Guid.NewGuid().ToString(),
            ["creditRevisionId"] = Guid.NewGuid().ToString(),
            ["documentDate"] = date,
            ["effectiveDate"] = date,
            ["reference"] = Guid.NewGuid().ToString(),
            ["reason"] = "Supplier agreed a reduction to the reviewed service cost",
            ["total"] = amount,
            ["claims"] = new JsonArray(new JsonObject
            {
                ["claimId"] = Guid.NewGuid().ToString(),
                ["billId"] = bill.ToString(),
                ["billRevisionId"] = revision.ToString(),
                ["recognitionEventId"] = invoice.ToString(),
                ["componentKey"] = "base",
                ["amount"] = amount
            }),
            ["duplicateResolutions"] = new JsonArray(),
            ["allocations"] = new JsonArray(),
            ["evidence"] = new JsonObject { ["documents"] = new JsonArray(), ["missingEvidenceReason"] = "Credit note supplied without a file" }
        };
    }

    public async Task<(Guid BillId, Guid InvoiceEventId, Guid UnitId)> PrepaymentBillAsync(string amount)
    {
        var draft = Bills.CompleteDraft(Guid.NewGuid().ToString());
        var revision = draft["revision"]!;
        revision["total"] = amount;
        revision["units"]![0]!["classification"] = "Expense";
        revision["units"]![0]!["components"]![0]!["amount"] = amount;
        revision["units"]![0]!["components"]![1]!["amount"] = "0";
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(Bills, draft);
        await Bills.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), Bills.PostCommand(reviewed));
        var bill = Guid.Parse(reviewed["billId"]!.GetValue<string>());
        var invoice = await Bills.ScalarAsync<Guid>($"SELECT EventId FROM Purchasing.SupplierBillPostingEvents WHERE BillId='{bill}'");
        var unit = await Bills.ScalarAsync<Guid>($"SELECT UnitId FROM Purchasing.RecognitionSideEvents WHERE Id='{invoice}'");
        return (bill, invoice, unit);
    }

    public async Task RecognizeAsync(Guid unit, string cost, string date = "2026-09-17")
    {
        var receipt = await Bills.Recognition.CommandAsync(classification: "Expense", cost: cost);
        receipt["postingDate"] = date;
        receipt["units"]![0]!["unitId"] = unit.ToString();
        receipt["units"]![0]!["expectedPriorEventRevision"] = 1;
        await Bills.Recognition.PostAsync(receipt.ToJsonString());
    }

    public async Task<JsonObject> CreditAsync(Guid bill, string amount, string date = "2026-09-16")
        => await Bills.ExecuteAsync("PostSupplierCredit", Guid.NewGuid(), await CreditCommandAsync(bill, amount, date));

    public Task<decimal> BalanceAsync(Guid item) => Payments.Allocation.BalanceAsync(item);

    public async Task<JsonObject> RefundCommandAsync(IReadOnlyList<(Guid ItemId, string Amount)> settlements, string date = "2026-09-18")
    {
        var amount = settlements.Sum(x => decimal.Parse(x.Amount, System.Globalization.CultureInfo.InvariantCulture));
        var command = await Payments.CommandAsync(amount.ToString(System.Globalization.CultureInfo.InvariantCulture), date);
        command["operation"] = "RecordSupplierRefund";
        command["cashSourceId"] = command["paymentId"]!.DeepClone(); command.Remove("paymentId");
        command["cashSourceRevisionId"] = command["paymentRevisionId"]!.DeepClone(); command.Remove("paymentRevisionId");
        command["actualDate"] = command["paymentDate"]!.DeepClone(); command.Remove("paymentDate");
        command.Remove("allocations");
        var targets = new JsonArray();
        foreach (var settlement in settlements) targets.Add(new JsonObject
        {
            ["itemId"] = settlement.ItemId.ToString(),
            ["expectedItemVersion"] = await Payments.Allocation.VersionAsync(settlement.ItemId),
            ["amount"] = settlement.Amount
        });
        command["settlements"] = targets;
        return command;
    }

    public async Task<JsonObject> RefundAsync(IReadOnlyList<(Guid ItemId, string Amount)> settlements, string date = "2026-09-18")
        => await Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), await RefundCommandAsync(settlements, date));

    public Task<string> CashEvidenceAsync() => Bills.ScalarAsync<string>("""
        SELECT COALESCE((SELECT source.*,JSON_QUERY((SELECT journal.* FROM Accounting.SourceEvents sourceEvent
          JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id
          WHERE sourceEvent.TenantId=source.TenantId AND sourceEvent.SourceId=source.Id
            AND sourceEvent.SourceKind=source.Kind ORDER BY journal.Sequence FOR JSON PATH)) journals
        FROM Purchasing.SupplierCashSources source ORDER BY source.Id FOR JSON PATH),N'[]')
        """);
    public async Task<JsonObject> ExecuteRawAsync(string json, Guid? request = null)
    {
        await using var command = new SqlCommand("EXEC Purchasing.PostSupplierCredit @ActorId,@SessionId,@RequestId,@Command", Bills.Journal.Connection);
        command.Parameters.AddWithValue("@ActorId", JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@SessionId", Bills.Journal.SessionId);
        command.Parameters.AddWithValue("@RequestId", request ?? Guid.NewGuid());
        command.Parameters.AddWithValue("@Command", json);
        return JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!.AsObject();
    }
    public Task<decimal> BankBalanceAsync() => Bills.ScalarAsync<decimal>($"SELECT COALESCE(SUM(Debit-Credit),0) FROM Accounting.JournalLines WHERE AccountId='{Bank}'");
    public Task<string> ReadOriginalBillEvidenceAsync(Guid bill) => Bills.ScalarAsync<string>($"SELECT (SELECT r.*,JSON_QUERY((SELECT p.* FROM Purchasing.SupplierBillPostings p WHERE p.TenantId=r.TenantId AND p.BillId=r.BillId FOR JSON PATH)) posting FROM Purchasing.SupplierBillRevisions r WHERE r.BillId='{bill}' ORDER BY r.Sequence FOR JSON PATH)");
    public ValueTask DisposeAsync() => Payments.DisposeAsync();
}
