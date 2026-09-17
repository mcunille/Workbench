// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    private static JsonNode FinancialInput()
    {
        var input = JsonNode.Parse(DraftOrderInputV4.Canonical("Create", null, null,
            DraftOrderInputV4.Normalize(DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line with { PriceMode = "lineTotal", Price = "100.0000" }] })))!;
        input["draft"]!["orderDiscount"] = null; input["draft"]!["charges"] = new JsonArray();
        input["draft"]!["entries"]![0]!["discount"] = null;
        return input;
    }

    private static JsonNode FinancialCharge(string category = "shipping") => new JsonObject
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["category"] = category,
        ["label"] = "Source charge",
        ["amount"] = "5.0000",
        ["payeeKind"] = "supplier",
        ["payeeName"] = null,
        ["amountStatus"] = "estimated",
        ["reference"] = null,
        ["notes"] = null
    };

    private static async Task<(Guid Id, byte[] Version, bool Replayed)> SaveFinancial(SqlConnection connection, Guid actor, Guid request, JsonNode input)
    {
        await using var command = new SqlCommand("Purchasing." + input["operation"]!.GetValue<string>() + "DraftOrderV4", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", request); command.Parameters.AddWithValue("@ActorUserId", actor);
        command.Parameters.AddWithValue("@CanonicalInputJson", input.ToJsonString());
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        return (reader.GetGuid(reader.GetOrdinal("DraftOrderId")), (byte[])reader["SavedVersion"], reader.GetBoolean(reader.GetOrdinal("Replayed")));
    }

    [Theory]
    [InlineData("\"mode\":\"fixed\"", "\"mode\":\"fixed\",\"mode\":\"fixed\"")]
    [InlineData("\"category\":\"shipping\"", "\"category\":\"shipping\",\"category\":\"shipping\"")]
    [InlineData("\"notes\":null", "\"notes\":null,\"notes\":null")]
    public async Task FinancialBoundaryRejectsDuplicateJsonKeys(string before, string after)
    {
        // GIVEN financial input sent directly to SQL with duplicate JSON properties.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var input = FinancialInput(); input["draft"]!["charges"]!.AsArray().Add(FinancialCharge());
        input["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"1.0000\"}");
        await using var command = new SqlCommand("Purchasing.CreateDraftOrderV4", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", Guid.NewGuid()); command.Parameters.AddWithValue("@ActorUserId", actor);
        command.Parameters.AddWithValue("@CanonicalInputJson", input.ToJsonString().Replace(before, after, StringComparison.Ordinal));
        // WHEN the restricted command validates the raw document THEN duplicates are rejected rather than resolved first/last.
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
    }

    [Fact]
    public async Task FinancialBoundaryValidatesEveryAdjustmentAndKnownBaseWithoutBlockingIncompleteDrafts()
    {
        // GIVEN the real restricted SQL boundary and a known merchandise base of 100.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var input = FinancialInput();
        var invalid = new Action<JsonNode>[]
        {
            n => n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"100.0001\"}"),
            n => n["draft"]!["entries"]![0]!["discount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"100.0001\"}"),
            n => n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"percentage\",\"value\":\"100.0001\"}"),
            n => n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"-1.0000\"}"),
            n => n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"01.0000\"}"),
            n => n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"1.00000\"}"),
            n => n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed \",\"value\":\"1.0000\"}"),
            n => n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":null}"),
            n => n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"1.0000\",\"extra\":null}"),
            n => n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value \":\"1.0000\"}"),
            n => n["draft"]!["entries"]![0]!.AsObject().Remove("discount"),
            n => n["draft"]!["entries"]![0]!["discount"] = "10.0000",
            n => n["draft"]!["extra"] = null,
            n => n["draft"]!["charges"]![0]!["category"] = "shipping ",
            n => n["draft"]!["charges"]![0]!["category"] = "unrecognized",
            n => n["draft"]!["charges"]![0]!["amount"] = "-1.0000",
            n => n["draft"]!["charges"]![0]!["amount"] = "10000000000000000000.0000",
            n => n["draft"]!["charges"]![0]!["amount"] = 5,
            n => n["draft"]!["charges"]![0]!["label"] = " ",
            n => n["draft"]!["charges"]![0]!["label"] = new string('x', 201),
            n => n["draft"]!["charges"]![0]!["payeeKind"] = "thirdParty",
            n => n["draft"]!["charges"]![0]!["payeeName"] = "Unexpected supplier payee",
            n => n["draft"]!["charges"]![0]!["amountStatus"] = "paid",
            n => { n["draft"]!["charges"]![0]!["amountStatus"] = "confirmed"; n["draft"]!["charges"]![0]!["amount"] = null; },
            n => n["draft"]!["charges"]![0]!["notes"] = new string('x', 2001),
            n => n["draft"]!["charges"]![0]!.AsObject().Remove("reference"),
            n => n["draft"]!["charges"]!.AsArray().Add(n["draft"]!["charges"]![0]!.DeepClone()),
            n => { for (var i = 1; i < 51; i++) n["draft"]!["charges"]!.AsArray().Add(FinancialCharge()); },
            n => n["draft"]!["currency"] = null,
            n => { n["draft"]!["entries"]![0]!["price"] = "0.0001"; n["draft"]!["entries"]![0]!["discount"] = JsonNode.Parse("{\"mode\":\"percentage\",\"value\":\"50.0000\"}"); n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"0.0001\"}"); },
            n => { n["draft"]!["entries"]![0]!["price"] = "9999999999999999999.9999"; n["draft"]!["entries"]![0]!["discount"] = JsonNode.Parse("{\"mode\":\"percentage\",\"value\":\"99.9999\"}"); n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"10000000000000.0001\"}"); },
            n => { n["draft"]!["entries"]![0]!["discount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"95.0000\"}"); n["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"5.0001\"}"); }
        };
        // WHEN each malformed or inconsistent financial input bypasses the API THEN SQL rejects it.
        foreach (var mutate in invalid)
        {
            var candidate = input.DeepClone(); candidate["draft"]!["charges"]!.AsArray().Add(FinancialCharge()); mutate(candidate);
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => SaveFinancial(connection, actor, Guid.NewGuid(), candidate))).Number);
        }
        // AND all category/payee combinations, separate tax rows, zero and 100 percent remain valid.
        foreach (var category in new[] { "shipping", "handling", "insurance", "salesTax", "vatGst", "customsDuty", "otherTax", "brokerage", "paymentFee", "inspection", "other" })
        {
            var row = FinancialCharge(category); row["payeeKind"] = "thirdParty"; row["payeeName"] = "Carrier";
            input["draft"]!["charges"]!.AsArray().Add(row);
        }
        input["draft"]!["charges"]!.AsArray().Add(FinancialCharge("salesTax"));
        input["draft"]!["charges"]![4]!["label"] = "Import VAT";
        input["draft"]!["entries"]![0]!["discount"] = JsonNode.Parse("{\"mode\":\"percentage\",\"value\":\"100.0000\"}");
        input["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"0.0000\"}");
        await SaveFinancial(connection, actor, Guid.NewGuid(), input);
        // AND an unknown merchandise base or charge stays saveable, retaining its unvalidated adjustment.
        input["draft"]!["entries"]![0]!["price"] = null;
        input["draft"]!["entries"]![0]!["discount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"9999999999999999999.9999\"}");
        input["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"9999999999999999999.9999\"}");
        input["draft"]!["charges"]![0]!["amount"] = null;
        await SaveFinancial(connection, actor, Guid.NewGuid(), input);
        // AND percentage-only incomplete drafts do not require a currency.
        var percentageOnly = FinancialInput(); percentageOnly["draft"]!["currency"] = null; percentageOnly["draft"]!["entries"]![0]!["price"] = null;
        percentageOnly["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"percentage\",\"value\":\"10.0000\"}");
        await SaveFinancial(connection, actor, Guid.NewGuid(), percentageOnly);
        // AND maximum-scale percentage arithmetic retains the exact rounded net of 10 trillion.
        var precise = FinancialInput(); precise["draft"]!["entries"]![0]!["price"] = "9999999999999999999.9999";
        precise["draft"]!["entries"]![0]!["discount"] = JsonNode.Parse("{\"mode\":\"percentage\",\"value\":\"99.9999\"}");
        precise["draft"]!["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"10000000000000.0000\"}");
        await SaveFinancial(connection, actor, Guid.NewGuid(), precise);
        // AND totals exceeding the 21 integer digit boundary cannot overflow or clamp into a save.
        var overflow = FinancialInput(); var lines = overflow["draft"]!["entries"]!.AsArray(); lines[0]!["price"] = "9999999999999999999.9999";
        for (var i = 1; i < 100; i++) { var line = lines[0]!.DeepClone(); line["id"] = Guid.NewGuid().ToString(); lines.Add(line); }
        overflow["draft"]!["charges"]!.AsArray().Add(FinancialCharge());
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => SaveFinancial(connection, actor, Guid.NewGuid(), overflow))).Number);
    }

    [Fact]
    public async Task FinancialTransitionRequiresFreshExplanationAndTwoSavedCurrencySteps()
    {
        // GIVEN a persisted confirmed charge with an earlier explanation and a saved currency.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var input = FinancialInput(); var charge = FinancialCharge(); charge["amountStatus"] = "confirmed"; charge["notes"] = "Original source";
        input["draft"]!["charges"]!.AsArray().Add(charge);
        var saved = await SaveFinancial(connection, actor, Guid.NewGuid(), input);
        input["operation"] = "Update"; input["targetId"] = saved.Id.ToString(); input["expectedVersion"] = Convert.ToBase64String(saved.Version);
        // WHEN amount, payee or confirmation changes reuse old notes THEN the command refuses the correction.
        foreach (var mutate in new Action<JsonNode>[] { c => c["amount"] = "6.0000", c => c["amountStatus"] = "estimated", c => { c["payeeKind"] = "thirdParty"; c["payeeName"] = "Bank"; } })
        {
            var candidate = input.DeepClone(); mutate(candidate["draft"]!["charges"]![0]!);
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => SaveFinancial(connection, actor, Guid.NewGuid(), candidate))).Number);
        }
        // AND clearing existing amounts in the same currency edit cannot relabel saved amounts.
        input["draft"]!["entries"]![0]!["price"] = null; charge["amount"] = null; charge["amountStatus"] = "estimated"; charge["notes"] = "Original source\nCleared for currency correction";
        input["draft"]!["currency"] = "EUR";
        Assert.Equal(50401, (await Assert.ThrowsAsync<SqlException>(() => SaveFinancial(connection, actor, Guid.NewGuid(), input))).Number);
        // WHEN clearing is saved first with new explanation THEN changing currency in a second save succeeds.
        input["draft"]!["currency"] = "USD"; saved = await SaveFinancial(connection, actor, Guid.NewGuid(), input);
        input["expectedVersion"] = Convert.ToBase64String(saved.Version); input["draft"]!["currency"] = "EUR";
        await SaveFinancial(connection, actor, Guid.NewGuid(), input);
    }

    [Fact]
    public async Task FinancialMigrationPreservesOldContentAndReceiptsAndRefusesDestructiveRollback()
    {
        // GIVEN an actual predecessor database with an old-shape V4 successful request.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("AddSupplierBasedDraftPricing");
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var request = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var old = FinancialInput(); old["draft"]!.AsObject().Remove("orderDiscount"); old["draft"]!.AsObject().Remove("charges"); old["draft"]!["entries"]![0]!.AsObject().Remove("discount");
        var original = await SaveFinancial(connection, actor, request, old);
        // WHEN upgraded THEN immutable old receipt replay still returns its original version.
        await Workbench.Server.Persistence.DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        var replay = await SaveFinancial(connection, actor, request, old);
        Assert.Equal(original.Id, replay.Id); Assert.Equal(original.Version, replay.Version); Assert.True(replay.Replayed);
        await using var read = new SqlCommand("SELECT ContentSchemaVersion FROM Purchasing.DraftOrders WHERE Id=@id", connection);
        read.Parameters.AddWithValue("@id", original.Id); Assert.Equal(3, Convert.ToInt32(await read.ExecuteScalarAsync()));
        // AND the same stale shape under a new request cannot create another draft.
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => SaveFinancial(connection, actor, Guid.NewGuid(), old))).Number);
        // AND downgrading explicitly fails rather than discarding financial content or its protection.
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => Workbench.Server.Persistence.DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddSupplierBasedDraftPricing", default))).Number);
    }

    [Fact]
    public async Task FinancialCommandPersistsAdjustmentsAndRejectsOldShapeNewWrites()
    {
        // GIVEN a restricted writer and a complete merchandise line with a financial adjustment.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var input = JsonNode.Parse(DraftOrderInputV4.Canonical("Create", null, null,
            DraftOrderInputV4.Normalize(DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line] })))!;
        var draft = input["draft"]!;
        draft["orderDiscount"] = JsonNode.Parse("{\"mode\":\"percentage\",\"value\":\"10.0000\"}");
        draft["charges"] = new JsonArray();
        draft["entries"]![0]!["discount"] = null;
        // WHEN saved through the restricted command THEN all adjustments survive in schema four.
        await using var command = new SqlCommand("Purchasing.CreateDraftOrderV4", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", Guid.NewGuid()); command.Parameters.AddWithValue("@ActorUserId", actor);
        command.Parameters.AddWithValue("@CanonicalInputJson", input.ToJsonString());
        Guid id;
        await using (var reader = await command.ExecuteReaderAsync()) { Assert.True(await reader.ReadAsync()); id = reader.GetGuid(reader.GetOrdinal("DraftOrderId")); }
        await using var read = new SqlCommand("SELECT ContentSchemaVersion FROM Purchasing.DraftOrders WHERE Id=@id", connection);
        read.Parameters.AddWithValue("@id", id); Assert.Equal(4, Convert.ToInt32(await read.ExecuteScalarAsync()));
        // AND a new request from a stale client cannot silently erase financial inputs.
        draft.AsObject().Remove("orderDiscount"); draft.AsObject().Remove("charges"); draft["entries"]![0]!.AsObject().Remove("discount");
        command.Parameters["@RequestId"].Value = Guid.NewGuid(); command.Parameters["@CanonicalInputJson"].Value = input.ToJsonString();
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
    }
}
