// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SupplierCreditLegacyFixture(SqlServerFixture server) : FinancialEvidenceLegacyFixture(server, PriorMigration)
{
    internal const string PriorMigration = "20261008010000_AddFinancialEvidenceRetention";
    internal const string ReleaseMigration = "20261019010000_AddSupplierCreditsAndRefunds";
    internal Guid BillId { get; private set; }
    internal Guid PaymentRequest { get; private set; }
    internal string PaymentInput { get; private set; } = "";
    internal string PaymentReceipt { get; private set; } = "";

    internal override async Task PrepareAsync(SupplierPaymentTestContext context)
    {
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context.Bills, storage);
        BillId = await context.Allocation.BillAsync("150");
        var payment = await context.CommandAsync("100");
        payment["evidence"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        PaymentRequest = Guid.NewGuid();
        PaymentInput = payment.ToJsonString();
        PaymentReceipt = (await context.RecordAsync(payment, PaymentRequest)).ToJsonString();
    }

    internal Task<JsonObject> ReplayPaymentAsync(SupplierPaymentTestContext context) =>
        context.RecordAsync(JsonNode.Parse(PaymentInput)!.AsObject(), PaymentRequest);
}
