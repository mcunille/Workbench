// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed partial class SupplierCreditTestContext
{
    public async Task<BillCreditProposal> PrepareBillAndCreditSuccessorsAsync()
    {
        await EnableBillCorrectionsAsync();
        var bill = await ExpenseBillAsync("100"); var original = await CreditAsync(bill, "18");
        var credit = Guid.Parse(original["sourceId"]!.ToString());
        var clearing = Guid.Parse((await RefundAsync([(credit, "18")]))["settlementIds"]![0]!.ToString());
        var replacement = await BillReplacementAsync(bill, "90"); var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        var newCredit = await CreditCommandAsync(bill, "10", "2026-09-20"); var successorCredit = Guid.Parse(newCredit["creditId"]!.ToString());
        var claim = newCredit["claims"]![0]!;
        claim["billId"] = successor.ToString(); claim["billRevisionId"] = replacement["post"]!["revisionId"]!.DeepClone();
        claim["recognitionEventId"] = replacement["unitBindings"]![0]!["invoiceEventId"]!.DeepClone();
        var command = await CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(await MatchedReceiptDecisionAsync(bill, replacement));
        command["sourceDecisions"]!.AsArray().Add(new JsonObject
        {
            ["sourceKind"] = "SupplierCredit",
            ["sourceId"] = credit.ToString(),
            ["expectedSourceVersion"] = (await CorrectionCommandAsync("Credit", credit, null))["expectedSourceVersion"]!.DeepClone(),
            ["replacement"] = newCredit
        });
        command["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = successorCredit.ToString(),
            ["debtItemId"] = clearing.ToString(),
            ["amount"] = "10"
        });
        return new BillCreditProposal(bill, credit, clearing, successor, successorCredit, command);
    }
}

internal sealed record BillCreditProposal(Guid Bill, Guid Credit, Guid Clearing, Guid SuccessorBill, Guid SuccessorCredit, JsonObject Command);
