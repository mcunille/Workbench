// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed partial class SupplierCreditTestContext
{
    public async Task EnableBillCorrectionsAsync()
    {
        await Bills.AdminAsync("""
        INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
          SELECT TenantId,RoleId,N'workbench/permission',N'SupplierBillsCorrect'
          FROM Administration.AccountingRoles WHERE Kind='Administrator';
        IF OBJECT_ID(N'Purchasing.PreviewSupplierBillCorrection') IS NOT NULL
          GRANT EXECUTE ON Purchasing.PreviewSupplierBillCorrection TO workbench_web;
        IF OBJECT_ID(N'Purchasing.CorrectSupplierBill') IS NOT NULL
          GRANT EXECUTE ON Purchasing.CorrectSupplierBill TO workbench_web;
        """);
        await Bills.AdminAsync(BillRecognitionProducerSql.Validator);
        await Bills.AdminAsync(BillRecognitionProducerSql.Register);
    }

    public async Task<Guid> InvoiceOnlyBillAsync(string amount, string classification = "Expense")
    {
        var draft = Bills.CompleteDraft(Guid.NewGuid().ToString());
        var revision = draft["revision"]!;
        revision["total"] = amount;
        revision["postingDate"] = "2026-09-15";
        revision["units"]![0]!["classification"] = classification;
        revision["units"]![0]!["components"]![0]!["amount"] = amount;
        revision["units"]![0]!["components"]![1]!["amount"] = "0";
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(Bills, draft);
        await Bills.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), Bills.PostCommand(reviewed));
        return Guid.Parse(reviewed["billId"]!.ToString());
    }

    public async Task<JsonObject> BillReplacementAsync(Guid bill, string amount, string date = "2026-09-20",
        string? documentDate = null, IReadOnlyDictionary<string, string>? unitCosts = null)
    {
        var original = JsonNode.Parse(await Bills.ScalarAsync<string>($"""
            SELECT revision.Payload FROM Purchasing.SupplierBillPostings posting
              JOIN Purchasing.SupplierBillRevisions revision ON revision.TenantId=posting.TenantId AND revision.Id=posting.RevisionId
              WHERE posting.BillId='{bill}'
            """))!.AsObject();
        var draft = Bills.DraftCommand(Guid.NewGuid().ToString());
        draft["revision"] = original.DeepClone();
        var revision = draft["revision"]!;
        revision["reference"] = Guid.NewGuid().ToString();
        revision["postingDate"] = date;
        if (documentDate is not null) revision["documentDate"] = documentDate;
        revision["total"] = amount;
        var bindings = new JsonArray();
        foreach (var unit in revision["units"]!.AsArray())
        {
            var successor = Guid.NewGuid().ToString();
            bindings.Add(new JsonObject
            {
                ["originalUnitId"] = unit!["unitId"]!.DeepClone(),
                ["successorUnitId"] = successor,
                ["invoiceEventId"] = Guid.NewGuid().ToString(),
                ["correctionGroupId"] = Guid.NewGuid().ToString()
            });
            unit["unitId"] = successor;
            unit["expectedPriorEventRevision"] = 0;
            unit["components"]![0]!["amount"] = unitCosts is null ? amount : unitCosts[unit["componentKey"]!.ToString()];
        }
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(Bills, draft);
        return new JsonObject { ["post"] = Bills.PostCommand(reviewed), ["unitBindings"] = bindings };
    }

    public async Task CaptureBillCorrectionPlanAsync(JsonObject command)
        => command["expectedPlanFingerprint"] = (await PreviewCorrectionAsync("Bill", command))["planFingerprint"]!.DeepClone();

    public async Task<JsonObject> MatchedReceiptDecisionAsync(Guid bill, JsonObject? replacement, bool retainWithoutBill = false)
    {
        var original = JsonNode.Parse(await Bills.ScalarAsync<string>($"""
            SELECT (SELECT receipt.Id eventId,receipt.SourceId sourceId,receipt.SourceComponentKey componentKey,
              CONVERT(varchar(18),CONVERT(binary(8),receipt.EventRevision),1) version,unit.Classification classification,
              CONVERT(nvarchar(60),(SELECT SUM(component.Amount) FROM Purchasing.RecognitionComponents component
                WHERE component.TenantId=receipt.TenantId AND component.EventId=receipt.Id AND component.Kind='BaseCost')) cost
            FROM Purchasing.SupplierBillPostingEvents posting JOIN Purchasing.RecognitionSideEvents invoice
              ON invoice.TenantId=posting.TenantId AND invoice.Id=posting.EventId
            JOIN Purchasing.RecognitionSideEvents receipt ON receipt.TenantId=invoice.TenantId AND receipt.UnitId=invoice.UnitId AND receipt.Side='Recognition'
            JOIN Purchasing.RecognitionUnits unit ON unit.TenantId=receipt.TenantId AND unit.Id=receipt.UnitId
            WHERE posting.BillId='{bill}' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
            """))!;
        JsonObject? successor = null;
        if (replacement is not null || retainWithoutBill)
        {
            var prepared = await Bills.Recognition.CommandAsync(classification: original["classification"]!.ToString(), cost: original["cost"]!.ToString());
            var side = prepared["units"]![0]!["sides"]![0]!;
            var preparedId = side["sourceId"]!.ToString();
            side["sourceId"] = original["sourceId"]!.DeepClone();
            await Bills.Recognition.PublishSourceRevisionAsync(side, preparedId);
            successor = new JsonObject
            {
                ["sourceRevision"] = side["sourceRevision"]!.DeepClone(),
                ["successorUnitId"] = replacement is null ? Guid.NewGuid().ToString() : replacement["unitBindings"]![0]!["successorUnitId"]!.DeepClone(),
                ["recognitionEventId"] = Guid.NewGuid().ToString()
            };
        }
        return new JsonObject
        {
            ["sourceKind"] = "PurchaseRecognition",
            ["sourceId"] = original["eventId"]!.DeepClone(),
            ["expectedSourceVersion"] = original["version"]!.DeepClone(),
            ["replacement"] = successor
        };
    }

    public Task<string> PaymentCashEvidenceAsync() => Bills.ScalarAsync<string>("""
        SELECT (SELECT payment.*,JSON_QUERY((SELECT source.*,JSON_QUERY((SELECT journal.*,
            JSON_QUERY((SELECT line.* FROM Accounting.JournalLines line
              WHERE line.TenantId=journal.TenantId AND line.JournalId=journal.Id ORDER BY line.Ordinal FOR JSON PATH)) lines
          FROM Accounting.JournalEntries journal WHERE journal.TenantId=source.TenantId AND journal.SourceEventId=source.Id
          ORDER BY journal.Id FOR JSON PATH)) journals
          FROM Accounting.SourceEvents source WHERE source.TenantId=payment.TenantId AND source.SourceKind='SupplierPayment'
            AND source.SourceId=payment.Id ORDER BY source.Id FOR JSON PATH)) cash
        FROM Purchasing.SupplierPayments payment ORDER BY payment.Id FOR JSON PATH)
        """);

    public Task<string> BillSourceEvidenceAsync(Guid bill) => Bills.ScalarAsync<string>($"""
        SELECT (SELECT bill.*,JSON_QUERY((SELECT revision.* FROM Purchasing.SupplierBillRevisions revision
          WHERE revision.TenantId=bill.TenantId AND revision.BillId=bill.Id ORDER BY revision.Id FOR JSON PATH)) revisions,
          JSON_QUERY((SELECT review.* FROM Purchasing.SupplierBillReviews review
          WHERE review.TenantId=bill.TenantId AND review.BillId=bill.Id ORDER BY review.Id FOR JSON PATH)) reviews,
          JSON_QUERY((SELECT evidence.* FROM Purchasing.SupplierBillEvidence evidence
          WHERE evidence.TenantId=bill.TenantId AND evidence.ReviewId IN
            (SELECT review.Id FROM Purchasing.SupplierBillReviews review WHERE review.TenantId=bill.TenantId AND review.BillId=bill.Id)
          ORDER BY evidence.RevisionId,evidence.DocumentId FOR JSON PATH)) evidence
        FROM Purchasing.SupplierBills bill WHERE bill.Id='{bill}' FOR JSON PATH)
        """);
}
