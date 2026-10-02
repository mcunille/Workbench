// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed class PurchaseRecognitionTestContext : IAsyncDisposable
{
    public JournalTestContext Journal { get; }
    public SqlConnection Connection => Journal.Connection;
    public Guid PurchaseOrderId { get; private set; } = Guid.NewGuid();
    public Guid SupplierId { get; private set; } = Guid.NewGuid();
    public string PurchaseOrderVersion { get; set; } = "";
    public Dictionary<string, Guid> Accounts { get; } = [];
    private PurchaseRecognitionTestContext(JournalTestContext journal) => Journal = journal;

    internal static PurchaseRecognitionTestContext Restore(JournalTestContext journal, SupplierContextState state)
    {
        var result = new PurchaseRecognitionTestContext(journal)
        {
            PurchaseOrderId = state.PurchaseOrderId,
            SupplierId = state.SupplierId,
            PurchaseOrderVersion = state.PurchaseOrderVersion
        };
        foreach (var account in JsonSerializer.Deserialize<Dictionary<string, Guid>>(state.AccountsJson)!)
            result.Accounts.Add(account.Key, account.Value);
        return result;
    }

    public static async Task<PurchaseRecognitionTestContext> OpenAsync(SqlServerFixture fixture, string? priorMigration = null)
    {
        var result = new PurchaseRecognitionTestContext(await JournalTestContext.OpenAsync(fixture, priorMigration));
        try
        {
            await using var admin = new SqlConnection(result.Journal.Application.AdminConnectionString);
            await admin.OpenAsync();
            await using (var install = new SqlCommand(PurchaseRecognitionAdapterSql.Install, admin))
                await install.ExecuteNonQueryAsync();
            await using (var seed = new SqlCommand("""
                INSERT Purchasing.Suppliers(Id,TenantId,Name,IsArchived,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId)
                VALUES(@supplier,@tenant,'Recognition supplier',0,SYSUTCDATETIME(),SYSUTCDATETIME(),@actor,@actor);
                INSERT Purchasing.DraftOrders(Id,TenantId,IsDeleted,State,OrderDate,Revision,SupplierId,Currency,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId)
                VALUES(@po,@tenant,0,'Ordered','2026-01-01',1,@supplier,'USD',4,'{}',SYSUTCDATETIME(),SYSUTCDATETIME(),@actor,@actor);
                INSERT Purchasing.PurchaseOrderRevisions(TenantId,DraftOrderId,Revision,OrderDate,ActorUserId,RecordedAtUtc,CalculationPolicyVersion,DraftJson,CalculationJson,PoReference)
                VALUES(@tenant,@po,1,'2026-01-01',@actor,SYSUTCDATETIME(),1,'{}','{}','PO-1');
                SELECT RowVersion FROM Purchasing.DraftOrders WHERE Id=@po;
                """, admin))
            {
                seed.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
                seed.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
                seed.Parameters.AddWithValue("@supplier", result.SupplierId);
                seed.Parameters.AddWithValue("@po", result.PurchaseOrderId);
                result.PurchaseOrderVersion = "0x" + Convert.ToHexString((byte[])(await seed.ExecuteScalarAsync())!);
            }
            var slots = new[] { "Expense", "Inventory", "Prepayment", "RecoverableTax", "GoodsReceivedNotInvoiced", "SupplierPayable" };
            var created = await result.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts", JsonSerializer.Serialize(slots.Select((slot, i) => new
            {
                code = (1000 + i).ToString(),
                name = slot,
                type = slot == "Expense" ? "Expense" : slot is "GoodsReceivedNotInvoiced" or "SupplierPayable" ? "Liability" : "Asset",
                purpose = slot == "SupplierPayable" ? "SupplierPayable" : "General"
            })));
            var ids = JsonSerializer.Deserialize<Guid[]>(created.Ids)!;
            for (var i = 0; i < slots.Length; i++) result.Accounts.Add(slots[i], ids[i]);
            await result.Journal.ConfigureAsync();
            // ConfigureAsync tracks the configuration version; retain it while adding fixture mappings.
            await using var map = new SqlCommand("UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,'$.mappings',JSON_QUERY(@mappings)) WHERE TenantId=@tenant", admin);
            map.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            map.Parameters.AddWithValue("@mappings", JsonSerializer.Serialize(result.Accounts.Select(x => new { slot = x.Key, accountId = x.Value })));
            await map.ExecuteNonQueryAsync();
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public async Task<JsonObject> CommandAsync(string side = "Recognition", string classification = "Inventory", string cost = "100", string tax = "0", bool eligible = true, string? recognitionBasis = null)
    {
        var evidence = new
        {
            schemaVersion = 1,
            rationale = "Reviewed source evidence",
            recognitionBasis = recognitionBasis ?? (classification == "Inventory" ? "ControlTransferred" : "ServicePerformed"),
            serviceDescription = "Performed service",
            serviceStartDate = "2026-01-01",
            serviceEndDate = "2026-02-01",
            controlTransferDate = "2026-02-01",
            inTransit = false,
            estimateBasis = "Approved supplier estimate",
            invoiceEligible = eligible,
            presentObligation = eligible,
            enforceableRight = eligible,
            taxPolicyReference = "Approved tax policy",
            taxEntitlement = true,
            sourceCapacityQuantity = "1",
            sourceCapacityAmount = (decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture) + decimal.Parse(tax, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var source = Guid.NewGuid(); var revision = Guid.NewGuid();
        var command = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1,
            operation = "Post",
            expectedConfigurationVersion = Journal.ConfigurationVersion,
            purchaseOrderId = PurchaseOrderId,
            expectedPurchaseOrderVersion = PurchaseOrderVersion,
            supplierId = SupplierId,
            currency = "USD",
            postingDate = "2026-02-01",
            units = new[] { new { unitId = Guid.NewGuid(), classification, goodsReference = "Goods-1", quantity = "1", quantityUnit = "each", expectedPriorEventRevision = 0,
                sides = new[] { new { side, eventRevision = 1, sourceId = source, sourceRevision = revision, sourceComponentKey = "line-1", subdivisionKey = "whole", sourceQuantity = "1", sourceAmount = evidence.sourceCapacityAmount,
                    documentDate = "2026-02-01", effectiveDate = "2026-02-01", evidence, components = new[] { new { componentKey = "base", kind = "BaseCost", amount = cost }, new { componentKey = "tax", kind = "RecoverableTax", amount = tax } } } } } }
        })!.AsObject();
        await using var admin = new SqlConnection(Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var store = new SqlCommand("INSERT Purchasing.FixtureRecognitionSources(TenantId,Id,Revision,PurchaseOrderId,SupplierId,Currency,Classification,Side,EvidenceJson,CorrectionAllowed) VALUES(@tenant,@id,@revision,@po,@supplier,'USD',@classification,@side,@evidence,1)", admin);
        store.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId); store.Parameters.AddWithValue("@id", source);
        store.Parameters.AddWithValue("@revision", revision); store.Parameters.AddWithValue("@po", PurchaseOrderId);
        store.Parameters.AddWithValue("@supplier", SupplierId); store.Parameters.AddWithValue("@classification", classification);
        store.Parameters.AddWithValue("@side", side); store.Parameters.AddWithValue("@evidence", JsonSerializer.Serialize(evidence));
        await store.ExecuteNonQueryAsync();
        await PublishSourceRevisionAsync(command["units"]![0]!["sides"]![0]!);
        return command;
    }

    public async Task PublishSourceRevisionAsync(JsonNode source, string? preparedSourceId = null)
    {
        await using var admin = new SqlConnection(Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new SqlCommand("EXEC Purchasing.PublishFixtureRecognitionRevision @TenantId=@tenant,@Side=@side,@SourceId=@id,@Revision=@revision,@PreparedSourceId=@prepared", admin);
        command.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        command.Parameters.AddWithValue("@side", source["side"]!.GetValue<string>());
        command.Parameters.AddWithValue("@id", source["sourceId"]!.GetValue<string>());
        command.Parameters.AddWithValue("@revision", source["sourceRevision"]!.GetValue<string>());
        command.Parameters.AddWithValue("@prepared", (object?)preparedSourceId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public Task<RecognitionResult> PostAsync(string commandJson, Guid? requestId = null, SqlConnection? connection = null)
        => ExecuteAsync("Post", commandJson, requestId, connection);
    public Task<RecognitionResult> CorrectAsync(string commandJson, Guid? requestId = null, SqlConnection? connection = null)
        => ExecuteAsync("Correct", commandJson, requestId, connection);
    private async Task<RecognitionResult> ExecuteAsync(string operation, string json, Guid? requestId, SqlConnection? connection)
    {
        await using var command = new SqlCommand($"EXEC Purchasing.{operation}FixtureRecognition @ActorId=@actor,@SessionId=@session,@RequestId=@request,@Command=@json", connection ?? Connection);
        command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); command.Parameters.AddWithValue("@session", Journal.SessionId);
        command.Parameters.AddWithValue("@request", requestId ?? Guid.NewGuid()); command.Parameters.AddWithValue("@json", json);
        return JsonSerializer.Deserialize<RecognitionResult>((string)(await command.ExecuteScalarAsync())!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    public async Task<decimal> BalanceAsync(string slot)
    {
        await using var command = new SqlCommand("SELECT COALESCE(SUM(Debit-Credit),0) FROM Accounting.JournalLines WHERE AccountId=@id", Connection);
        command.Parameters.AddWithValue("@id", Accounts[slot]); return (decimal)(await command.ExecuteScalarAsync())!;
    }
    public async Task<int> CountAsync(string table)
    {
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM Purchasing.[{table}]", Connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }
    public ValueTask DisposeAsync() => Journal.DisposeAsync();
}

internal sealed record RecognitionResult(Guid CommandId, Guid[] UnitIds, Guid[] EventIds, Guid[] MatchIds, Guid? CorrectionGroupId, Guid[] JournalIds, DateTimeOffset RecordedAtUtc);
