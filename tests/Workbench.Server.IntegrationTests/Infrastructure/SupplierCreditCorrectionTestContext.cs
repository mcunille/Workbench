// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed partial class SupplierCreditTestContext
{
    internal static async Task<SupplierCreditTestContext> RestoreAsync(SupplierPaymentTestContext payments)
    {
        var context = new SupplierCreditTestContext(payments);
        try
        {
            context.CreditAccount = await context.Bills.ScalarAsync<Guid>("SELECT Id FROM Accounting.Accounts WHERE Purpose='SupplierCreditReceivable'");
            context.ClearingAccount = await context.Bills.ScalarAsync<Guid>("SELECT Id FROM Accounting.Accounts WHERE Purpose='SupplierRefundClearing'");
            return context;
        }
        catch { await context.DisposeAsync(); throw; }
    }

    public async Task<JsonObject> CorrectionCommandAsync(string kind, Guid source, JsonObject? replacement, string date = "2026-09-20")
    {
        var versionQuery = kind switch
        {
            "Credit" => $"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierCreditVersions WHERE CreditId='{source}'",
            "Bill" => $"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierBills WHERE Id='{source}'",
            "Refund" or "RefundRepayment" => $"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierCashVersions WHERE CashSourceId='{source}'",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["operation"] = $"CorrectSupplier{kind}",
            ["expectedConfigurationVersion"] = Bills.Journal.ConfigurationVersion.ToString(),
            ["purchaseOrderId"] = Bills.Recognition.PurchaseOrderId.ToString(),
            ["expectedPurchaseOrderVersion"] = Bills.Recognition.PurchaseOrderVersion,
            ["supplierId"] = Bills.Recognition.SupplierId.ToString(),
            ["currency"] = "USD",
            ["postingDate"] = date,
            ["sourceId"] = source.ToString(),
            ["expectedSourceVersion"] = await Bills.ScalarAsync<string>(versionQuery),
            ["reason"] = "Replace the complete source after supplier review",
            ["replacement"] = replacement?.DeepClone(),
            ["sourceDecisions"] = new JsonArray(),
            ["reapplications"] = new JsonArray()
        };
    }

    public async Task<JsonObject> PreviewCorrectionAsync(string kind, JsonObject command)
    {
        if (kind is not ("Credit" or "Bill" or "Refund" or "RefundRepayment")) throw new ArgumentOutOfRangeException(nameof(kind));
        var preview = command.DeepClone().AsObject(); preview.Remove("expectedPlanFingerprint");
        await using var sql = new SqlCommand($"EXEC Purchasing.PreviewSupplier{kind}Correction @ActorId,@SessionId,@Command", Bills.Journal.Connection);
        sql.Parameters.AddWithValue("@ActorId", JournalTestContext.ActorId); sql.Parameters.AddWithValue("@SessionId", Bills.Journal.SessionId);
        sql.Parameters.AddWithValue("@Command", preview.ToJsonString());
        return JsonNode.Parse((string)(await sql.ExecuteScalarAsync())!)!.AsObject();
    }

    public Task<JsonObject> CorrectAsync(string kind, JsonObject command, Guid? request = null)
    {
        if (kind is not ("Credit" or "Bill" or "Refund" or "RefundRepayment")) throw new ArgumentOutOfRangeException(nameof(kind));
        return Bills.ExecuteAsync($"CorrectSupplier{kind}", request ?? Guid.NewGuid(), command);
    }

    public async Task CaptureCorrectionPlanAsync(JsonObject command)
        => command["expectedPlanFingerprint"] = (await PreviewCorrectionAsync("Credit", command))["planFingerprint"]!.DeepClone();

    public Task<string> CreditEvidenceAsync(Guid credit) => Bills.ScalarAsync<string>($"""
        SELECT (SELECT source.*,JSON_QUERY((SELECT claim.* FROM Purchasing.SupplierCreditClaims claim
          WHERE claim.TenantId=source.TenantId AND claim.CreditId=source.Id ORDER BY claim.Id FOR JSON PATH)) claims,
          JSON_QUERY((SELECT evidence.* FROM Accounting.FinancialEvidenceSets evidence
            WHERE evidence.TenantId=source.TenantId AND evidence.OwnerKind='SupplierCredit' AND evidence.OwnerId=source.Id
            ORDER BY evidence.Id FOR JSON PATH)) evidence
        FROM Purchasing.SupplierCredits source WHERE source.Id='{credit}' FOR JSON PATH)
        """);
}
