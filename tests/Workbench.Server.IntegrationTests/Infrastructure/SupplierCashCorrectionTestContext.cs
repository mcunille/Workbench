// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed partial class SupplierCreditTestContext
{
    public async Task EnableCashCorrectionsAsync()
    {
        await Bills.AdminAsync("""
            INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
              SELECT role.TenantId,role.RoleId,N'workbench/permission',permission.Name
              FROM Administration.AccountingRoles role
              CROSS JOIN(VALUES(N'SupplierRefundRepaymentsRecord'),(N'SupplierRefundsCorrect'),(N'SupplierRefundRepaymentsCorrect')) permission(Name)
              WHERE role.Kind='Administrator';
            IF OBJECT_ID(N'Purchasing.RecordSupplierRefundRepayment') IS NOT NULL
              GRANT EXECUTE ON Purchasing.RecordSupplierRefundRepayment TO workbench_web;
            IF OBJECT_ID(N'Purchasing.PreviewSupplierRefundCorrection') IS NOT NULL
              GRANT EXECUTE ON Purchasing.PreviewSupplierRefundCorrection TO workbench_web;
            IF OBJECT_ID(N'Purchasing.CorrectSupplierRefund') IS NOT NULL
              GRANT EXECUTE ON Purchasing.CorrectSupplierRefund TO workbench_web;
            IF OBJECT_ID(N'Purchasing.PreviewSupplierRefundRepaymentCorrection') IS NOT NULL
              GRANT EXECUTE ON Purchasing.PreviewSupplierRefundRepaymentCorrection TO workbench_web;
            IF OBJECT_ID(N'Purchasing.CorrectSupplierRefundRepayment') IS NOT NULL
              GRANT EXECUTE ON Purchasing.CorrectSupplierRefundRepayment TO workbench_web;
            """);
    }

    public async Task<JsonObject> RepaymentCommandAsync(IReadOnlyList<(Guid ItemId, string Amount)> settlements, string date = "2026-09-21")
    {
        var command = await RefundCommandAsync(settlements, date);
        command["operation"] = "RecordSupplierRefundRepayment";
        return command;
    }

    public async Task<JsonObject> RepaymentAsync(IReadOnlyList<(Guid ItemId, string Amount)> settlements, string date = "2026-09-21")
        => await Bills.ExecuteAsync("RecordSupplierRefundRepayment", Guid.NewGuid(), await RepaymentCommandAsync(settlements, date));

    public async Task CaptureCashCorrectionPlanAsync(string kind, JsonObject command)
        => command["expectedPlanFingerprint"] = (await PreviewCorrectionAsync(kind, command))["planFingerprint"]!.DeepClone();

    public Task<string> CashSourceEvidenceAsync(Guid id) => Bills.ScalarAsync<string>($"""
        SELECT (SELECT source.*,JSON_QUERY((SELECT evidence.* FROM Accounting.FinancialEvidenceSets evidence
          WHERE evidence.TenantId=source.TenantId AND evidence.OwnerKind=source.Kind AND evidence.OwnerId=source.Id
          ORDER BY evidence.Id FOR JSON PATH)) evidence
        FROM Purchasing.SupplierCashSources source WHERE source.Id='{id}' FOR JSON PATH)
        """);

    public async Task<(Guid Bill, Guid OriginalCredit, Guid SuccessorCredit, Guid Refund, Guid Clearing)> PrepareRefundClearingAsync(
        (Guid Document, Guid Revision)? document = null)
    {
        var bill = await ExpenseBillAsync("100");
        var creditCommand = await CreditCommandAsync(bill, "18");
        AddRecoveryEvidence(creditCommand, document);
        var credit = await Bills.ExecuteAsync("PostSupplierCredit", Guid.NewGuid(), creditCommand);
        var original = Guid.Parse(credit["sourceId"]!.ToString());
        var refundCommand = await RefundCommandAsync([(original, "18")]);
        AddRecoveryEvidence(refundCommand, document);
        var refund = await Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), refundCommand);
        var clearing = Guid.Parse(refund["settlementIds"]![0]!.ToString());
        var replacement = await CreditCommandAsync(bill, "10", "2026-09-20");
        var successor = Guid.Parse(replacement["creditId"]!.ToString());
        var correction = await CorrectionCommandAsync("Credit", original, replacement);
        correction["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = successor.ToString(),
            ["debtItemId"] = clearing.ToString(),
            ["amount"] = "10"
        });
        await CaptureCorrectionPlanAsync(correction); await CorrectAsync("Credit", correction);
        return (bill, original, successor, Guid.Parse(refund["sourceId"]!.ToString()), clearing);
    }

    internal static void AddRecoveryEvidence(JsonObject command, (Guid Document, Guid Revision)? document)
    {
        if (document is { } evidence)
            command["evidence"] = new JsonObject
            {
                ["documents"] = FinancialEvidencePostingTests.Documents(evidence),
                ["missingEvidenceReason"] = null
            };
    }
}
