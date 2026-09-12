// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Theory]
    [InlineData("envelope")]
    [InlineData("draft")]
    [InlineData("entry")]
    public async Task RestrictedCommandRequiresExactPropertyNamesWithinEveryObject(string scope)
    {
        // GIVEN a restricted tenant connection and a complete, canonical shopping list.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        using var canonical = JsonDocument.Parse(PropertyNameFixture());
        var draft = canonical.RootElement.GetProperty("draft");
        var target = scope switch { "envelope" => canonical.RootElement, "draft" => draft, _ => draft.GetProperty("entries")[0] };
        var keys = target.EnumerateObject().Select(property => property.Name).ToArray();
        foreach (var key in keys)
        {
            foreach (var variation in new[] { "space", "tab", "nonbreaking-space", "unknown", "duplicate", "padded-duplicate", "replacement-duplicate" })
            {
                var changedObject = MutateProperties(target, key, variation, keys.First(other => other != key));
                var changedDraft = scope == "entry" ? ReplaceJsonProperty(draft, "entries", "[" + changedObject + "]") : changedObject;
                var input = scope == "envelope" ? changedObject : ReplaceJsonProperty(canonical.RootElement, "draft", changedDraft);
                // WHEN any supported key is padded, replaced by an unknown key, or duplicated THEN SQL rejects the complete save.
                var error = await Record.ExceptionAsync(() => Save(connection, actor, Guid.NewGuid(), input, "Create"));
                Assert.True(error is SqlException { Number: 50400 }, $"Expected SQL validation for {scope}.{key} ({variation}); received {error?.GetType().Name ?? "success"}.");
            }
        }
        // AND malformed requests leave neither a draft nor a successful request receipt.
        await using var count = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.DraftOrders)+(SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts)", connection);
        Assert.Equal(0, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CanonicalPropertyNamesRoundTripAndReplayThroughTheRestrictedCommand()
    {
        // GIVEN a canonical draft whose entry includes every supported property.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var request = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var input = PropertyNameFixture();
        // WHEN SQL saves and replays the request THEN it preserves content consumable by the strict API DTO.
        var saved = await Save(connection, actor, request, input, "Create");
        var replay = await Save(connection, actor, request, input, "Create");
        Assert.True(replay.Replayed); Assert.Equal(saved.Version, replay.Version);
        await using var read = new SqlCommand("SELECT ContentJson FROM Purchasing.DraftOrders WHERE Id=@id", connection);
        read.Parameters.AddWithValue("@id", saved.Id);
        using var content = JsonDocument.Parse((string)(await read.ExecuteScalarAsync())!);
        var entries = content.RootElement.GetProperty("entries").Deserialize<DraftEntry[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(entries);
        Assert.Collection(entries,
            entry => { Assert.Equal("Stone", entry.Description); Assert.Equal("1.0000", entry.IndicativePrice); },
            entry => { Assert.Equal("Second stone", entry.Description); Assert.Equal("0.0000", entry.IndicativePrice); });
    }

    private static string PropertyNameFixture() => JsonSerializer.Serialize(new
    {
        operation = "Create",
        targetId = (string?)null,
        expectedVersion = (string?)null,
        draft = new
        {
            title = "Cart",
            supplierName = "Supplier",
            supplierId = (Guid?)null,
            supplierContactName = (string?)null,
            supplierEmail = (string?)null,
            supplierPhone = (string?)null,
            supplierWebsite = (string?)null,
            supplierPostalAddress = (string?)null,
            supplierOrderReference = (string?)null,
            platform = (string?)null,
            currency = "USD",
            notes = "Draft notes",
            sourceLinks = new[] { "https://supplier.example/cart" },
            entries = new[]
            {
                new { id = Guid.Parse("45850e40-50e9-41bd-b14a-cd1d4c352f80"), description = "Stone", notes = "Entry notes", sourceLink = "https://supplier.example/stone", indicativePrice = "1.0000" },
                new { id = Guid.Parse("45850e40-50e9-41bd-b14a-cd1d4c352f81"), description = "Second stone", notes = "Second entry notes", sourceLink = "https://supplier.example/second", indicativePrice = "0.0000" },
            },
        },
    });

    private static string ReplaceJsonProperty(JsonElement target, string key, string value) =>
        "{" + string.Join(",", target.EnumerateObject().Select(property => JsonSerializer.Serialize(property.Name) + ":" + (property.Name == key ? value : property.Value.GetRawText()))) + "}";

    private static string MutateProperties(JsonElement target, string key, string variation, string otherKey)
    {
        var renamed = variation switch { "space" => key + " ", "tab" => key + "\t", "nonbreaking-space" => key + "\u00a0", "unknown" => "unknown", "replacement-duplicate" => otherKey, _ => key };
        var properties = target.EnumerateObject().Select(property => JsonSerializer.Serialize(property.Name == key ? renamed : property.Name) + ":" + property.Value.GetRawText()).ToList();
        if (variation is "duplicate" or "padded-duplicate")
            properties.Add(JsonSerializer.Serialize(variation == "duplicate" ? key : key + " ") + ":" + target.GetProperty(key).GetRawText());
        return "{" + string.Join(",", properties) + "}";
    }
}
