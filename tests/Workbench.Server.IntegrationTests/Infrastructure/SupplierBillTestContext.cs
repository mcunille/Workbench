// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed class SupplierBillTestContext(PurchaseRecognitionTestContext recognition) : IAsyncDisposable
{
    public PurchaseRecognitionTestContext Recognition { get; } = recognition;
    public JournalTestContext Journal => Recognition.Journal;
    public static async Task<SupplierBillTestContext> OpenAsync(SqlServerFixture fixture, string? priorMigration = null)
    {
        var context = new SupplierBillTestContext(await PurchaseRecognitionTestContext.OpenAsync(fixture, priorMigration));
        try
        {
            await context.AdminAsync("""
                INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
                  SELECT TenantId,RoleId,N'workbench/permission',p.Permission
                  FROM Administration.AccountingRoles CROSS JOIN
                    (VALUES(N'SupplierBillsManage'),(N'SupplierBillsPost')) p(Permission) WHERE Kind='Administrator';
                GRANT EXECUTE ON Purchasing.SaveSupplierBill TO workbench_web;
                """);
            return context;
        }
        catch { await context.DisposeAsync(); throw; }
    }
    public JsonObject DraftCommand(string reference = "INV-1") => new()
    {
        ["schemaVersion"] = 1,
        ["operation"] = "Create",
        ["billId"] = Guid.NewGuid().ToString(),
        ["purchaseOrderId"] = Recognition.PurchaseOrderId.ToString(),
        ["supplierId"] = Recognition.SupplierId.ToString(),
        ["currency"] = "USD",
        ["expectedPurchaseOrderVersion"] = Recognition.PurchaseOrderVersion,
        ["revision"] = new JsonObject { ["kind"] = "Invoice", ["reference"] = reference }
    };
    public JsonObject CompleteDraft(string reference = "INV-1", string kind = "Invoice")
    {
        var command = DraftCommand(reference);
        var revision = command["revision"]!.AsObject();
        revision["kind"] = kind;
        revision["documentDate"] = "2026-02-01"; revision["effectiveDate"] = "2026-02-01";
        revision["postingDate"] = "2026-02-01"; revision["terms"] = "Due on receipt";
        revision["total"] = "110.00"; revision["missingEvidenceReason"] = "Reviewed invoice supplied without a file";
        revision["units"] = JsonNode.Parse("""
            [{"componentKey":"line-1","classification":"Inventory","goodsReference":"Goods-1","quantity":"1","quantityUnit":"each",
              "expectedPriorEventRevision":0,"components":[{"componentKey":"base","kind":"BaseCost","amount":"100.00"},
                {"componentKey":"tax","kind":"RecoverableTax","amount":"10.00"}],
              "evidence":{"rationale":"Reviewed obligation","invoiceEligible":true,"presentObligation":true,"enforceableRight":true,
                "taxPolicyReference":"Approved tax policy","taxEntitlement":true}}]
            """);
        revision["units"]![0]!["unitId"] = Guid.NewGuid().ToString();
        return command;
    }
    public JsonObject ReviewCommand(JsonObject saved)
    {
        var command = Change(saved, "Review");
        command["revisionId"] = saved["revisionId"]!.DeepClone();
        command["rationale"] = "Reviewed original supplier invoice and classification";
        command["resolutions"] = new JsonArray();
        return command;
    }
    public Task<JsonObject> ReviewAsync(Guid requestId, JsonObject command)
        => ExecuteAsync("ReviewSupplierBill", requestId, command);
    public async Task<(Guid DocumentId, Guid RevisionId)> SeedDocumentAsync()
    {
        var document = Guid.NewGuid(); var revision = Guid.NewGuid(); var attachment = Guid.NewGuid();
        await AdminAsync($"""
            INSERT Storage.Attachments(Id,TenantId,CreatedAtUtc) VALUES('{attachment}','{JournalTestContext.TenantId}',SYSUTCDATETIME());
            INSERT Storage.Revisions(Id,TenantId,AttachmentId,OperationId,ActorUserId,ProviderAlias,Source,MediaType,Length,Sha256,State,CreatedAtUtc)
              VALUES('{revision}','{JournalTestContext.TenantId}','{attachment}',NEWID(),'{JournalTestContext.ActorId}','local','PurchaseOrderDocumentOriginal','application/pdf',4,REPLICATE('A',64),1,SYSUTCDATETIME());
            UPDATE Storage.Attachments SET CurrentRevisionId='{revision}' WHERE Id='{attachment}';
            INSERT Purchasing.PurchaseOrderDocuments(Id,TenantId,OrderId,AttachmentId,RevisionId,Label,MediaType,Extension,Length,Sha256,CreatedAtUtc)
              VALUES('{document}','{JournalTestContext.TenantId}','{Recognition.PurchaseOrderId}','{attachment}','{revision}','Invoice','application/pdf','pdf',4,REPLICATE('A',64),SYSUTCDATETIME());
            """);
        return (document, revision);
    }
    public JsonObject PostCommand(JsonObject reviewed)
    {
        var command = Change(reviewed, "Post");
        command["revisionId"] = reviewed["revisionId"]!.DeepClone();
        command["expectedConfigurationVersion"] = Journal.ConfigurationVersion.ToString();
        return command;
    }
    public Task<JsonObject> SaveAsync(Guid requestId, JsonObject command, SqlConnection? connection = null)
        => ExecuteAsync("SaveSupplierBill", requestId, command, connection);
    public Task<JsonObject> ReadAsync(string procedure, params (string Name, object Value)[] parameters)
        => ReadAsync(Journal.Connection, JournalTestContext.ActorId, Journal.SessionId, procedure, parameters);

    internal static async Task<JsonObject> ReadAsync(SqlConnection connection, Guid actorId, Guid sessionId,
        string procedure, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand($"Purchasing.{procedure}", connection) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ActorId", actorId);
        command.Parameters.AddWithValue("@SessionId", sessionId);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!.AsObject();
    }

    public async Task<SupplierBillDetail> ReadDetailAsync(Guid billId)
        => (await ReadAsync("ReadSupplierBill", ("@BillId", billId))).Deserialize<SupplierBillDetail>(JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Bill query returned no result.");

    public async Task<JsonObject> ExecuteAsync(string procedure, Guid requestId, JsonObject command, SqlConnection? connection = null)
    {
        await using var sql = new SqlCommand($"EXEC Purchasing.{procedure} @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Command=@command", connection ?? Journal.Connection);
        sql.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
        sql.Parameters.AddWithValue("@session", Journal.SessionId);
        sql.Parameters.AddWithValue("@request", requestId);
        sql.Parameters.AddWithValue("@command", command.ToJsonString());
        return JsonNode.Parse((string)(await sql.ExecuteScalarAsync())!)!.AsObject();
    }
    public async Task AdminAsync(string sql)
    {
        await using var admin = new SqlConnection(Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new SqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync();
    }
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var admin = new SqlConnection(Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new SqlCommand(sql, admin);
        return (T)(await command.ExecuteScalarAsync())!;
    }
    public JsonObject Change(JsonObject created, string operation, JsonObject? revision = null)
    {
        var command = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["operation"] = operation,
            ["billId"] = created["billId"]!.DeepClone(),
            ["billVersion"] = created["version"]!.DeepClone(),
            ["expectedPurchaseOrderVersion"] = Recognition.PurchaseOrderVersion
        };
        if (revision is not null) command["revision"] = revision.DeepClone();
        if (operation == "Abandon") command["reason"] = "Duplicate entered by mistake";
        return command;
    }
    public ValueTask DisposeAsync() => Recognition.DisposeAsync();
}

internal sealed record SupplierBillEvidence(Guid DocumentId, Guid RevisionId, string Digest, long Length, string Label, bool Available);
internal sealed record SupplierBillDetail(Guid BillId, Guid PurchaseOrderId, Guid SupplierId, string Currency, string State,
    Guid RevisionId, string Version, string SupplierName, JsonElement Revision, JsonElement? Review, JsonElement? Posting, IReadOnlyList<SupplierBillEvidence> Evidence);
