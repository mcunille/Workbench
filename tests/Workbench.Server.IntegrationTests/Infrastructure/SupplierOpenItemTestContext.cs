// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using System.Data;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed class SupplierOpenItemTestContext(SupplierBillTestContext bills) : IAsyncDisposable
{
    public SupplierBillTestContext Bills { get; } = bills;
    public PurchaseRecognitionTestContext Recognition => Bills.Recognition;
    public JournalTestContext Journal => Bills.Journal;

    public static async Task<SupplierOpenItemTestContext> OpenAsync(SqlServerFixture fixture, string? priorMigration = null)
    {
        var context = new SupplierOpenItemTestContext(await SupplierBillTestContext.OpenAsync(fixture, priorMigration));
        try
        {
            await context.Bills.AdminAsync(SupplierOpenItemAdapterSql.Install);
            return context;
        }
        catch { await context.DisposeAsync(); throw; }
    }

    public async Task<JsonObject> ExecuteAsync(string operation, JsonObject command, SqlConnection? connection = null)
    {
        var procedure = operation == "AppendOpening" ? "AppendFixtureSupplierEventGroup" : operation;
        await using var sql = new SqlCommand($"EXEC Purchasing.{procedure} @ActorId=@actor,@SessionId=@session,@Command=@command",
            connection ?? Journal.Connection);
        sql.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
        sql.Parameters.AddWithValue("@session", Journal.SessionId);
        sql.Parameters.AddWithValue("@command", command.ToJsonString());
        return JsonNode.Parse((string)(await sql.ExecuteScalarAsync())!)!.AsObject();
    }

    public Task<T> ScalarAsync<T>(string sql) => Bills.ScalarAsync<T>(sql);

    public async Task<string> ValidateAsync(string operation, JsonObject command)
    {
        await using var admin = new SqlConnection(Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var sql = new SqlCommand("Purchasing.ValidateSupplierFinancialCommand", admin)
        {
            CommandType = CommandType.StoredProcedure
        };
        sql.Parameters.AddWithValue("@Operation", operation);
        sql.Parameters.AddWithValue("@Command", command.ToJsonString());
        var canonical = sql.Parameters.Add("@Canonical", SqlDbType.NVarChar, -1);
        canonical.Direction = ParameterDirection.Output;
        await sql.ExecuteNonQueryAsync();
        return (string)canonical.Value;
    }

    public async Task<JsonObject> PostedInvoiceOpeningAsync(string amount = "110.00")
    {
        var source = await Recognition.CommandAsync(side: "Invoice", cost: "100", tax: "10");
        var posted = await Recognition.PostAsync(source.ToJsonString());
        return new JsonObject
        {
            ["groupId"] = Guid.NewGuid().ToString(),
            ["recordedAtUtc"] = posted.RecordedAtUtc.ToString("O"),
            ["events"] = new JsonArray(new JsonObject
            {
                ["itemId"] = Guid.NewGuid().ToString(),
                ["recognitionEventId"] = posted.EventIds[0].ToString(),
                ["journalId"] = posted.JournalIds[0].ToString(),
                ["ordinal"] = await ScalarAsync<int>($"SELECT Ordinal FROM Accounting.JournalLines WHERE JournalId='{posted.JournalIds[0]}' AND AccountPurpose='SupplierPayable'"),
                ["amount"] = amount
            })
        };
    }

    public ValueTask DisposeAsync() => Bills.DisposeAsync();
}
