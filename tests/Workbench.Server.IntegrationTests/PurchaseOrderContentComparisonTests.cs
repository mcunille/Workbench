// Copyright (c) 2026 The White Stag Collection.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Fact]
    public async Task AmendmentsIgnoreJsonPresentationButPreserveNestedContentOrderAndDateChanges()
    {
        // GIVEN a committed order with nested discounts, charges, links and Unicode/slash text.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var line = DraftOrderPricingTests.Line with { Description = "Café / ruby", Discount = new("percentage", "5.0000") };
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with
        {
            SupplierName = "Supplier",
            Entries = [line, line with { Id = Guid.NewGuid(), Description = "Other ruby" }],
            SourceLinks = ["https://example.com/a", "https://example.com/b"],
            OrderDiscount = new("fixed", "1.0000"),
            Charges = [new(Guid.NewGuid(), "shipping", "Café / freight", "2.0000", "supplier", null, "estimated", null, null)]
        });
        var saved = await SaveFinancial(connection, actor, Guid.NewGuid(), JsonNode.Parse(DraftOrderInput.Canonical("Create", null, null, draft))!);
        var current = await Purchase(connection, actor, saved.Id, Guid.NewGuid(), saved.Version, "Commit", null);
        var ordinary = JsonSerializer.Serialize(draft, DraftOrderInput.JsonOptions);
        var presentation = ReverseProperties(JsonNode.Parse(ordinary))!.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Assert.NotEqual(ordinary, presentation);
        // WHEN property order, whitespace or escaping differs at any nesting level THEN no revision is created.
        foreach (var encoded in new[] { ordinary, presentation, presentation.Replace("/", "\\/", StringComparison.Ordinal) })
            Assert.Equal(50417, (await Assert.ThrowsAsync<SqlException>(() => Purchase(connection, actor, saved.Id, Guid.NewGuid(), current.Version, "Amend", draft, rawDraft: encoded))).Number);

        // WHEN actual nested values or array positions change THEN each accepted amendment advances history once.
        var changes = new[]
        {
            draft with { Entries = [line with { Description = "Cafe / ruby" }, draft.Entries[1]] },
            draft with { Entries = [line with { Discount = new("percentage", "6.0000") }, draft.Entries[1]] },
            draft with { Entries = [line with { Price = null }, draft.Entries[1]] },
            draft with { Entries = [line with { Price = "0.0000", Discount = null }, draft.Entries[1]] },
            draft with { Charges = [draft.Charges[0] with { Label = "Freight correction" }] },
            draft with { Charges = [] },
            draft with { OrderDiscount = new("fixed", "2.0000") },
            draft with { Entries = [draft.Entries[0]] },
            draft with { Entries = [draft.Entries[1], draft.Entries[0]] },
            draft with { SourceLinks = [draft.SourceLinks[1], draft.SourceLinks[0]] }
        };
        var expectedRevision = 1;
        foreach (var changed in changes)
        {
            current = await Purchase(connection, actor, saved.Id, Guid.NewGuid(), current.Version, "Amend", DraftOrderInput.Normalize(changed));
            Assert.Equal(++expectedRevision, current.Revision);
            current = await Purchase(connection, actor, saved.Id, Guid.NewGuid(), current.Version, "Amend", draft);
            Assert.Equal(++expectedRevision, current.Revision);
        }
        // AND changing only the order date remains a valid amendment.
        current = await Purchase(connection, actor, saved.Id, Guid.NewGuid(), current.Version, "Amend", draft, "2026-09-12");
        Assert.Equal(++expectedRevision, current.Revision);
        // AND the new comparison command stays internal to the privileged purchase command.
        await using var denied = new SqlCommand("DECLARE @same bit; EXEC Purchasing.ComparePurchaseOrderContent N'{}',N'{}',@same OUTPUT", connection);
        Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
    }

    private static JsonNode? ReverseProperties(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Reverse().Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, ReverseProperties(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(ReverseProperties).ToArray()),
        _ => node?.DeepClone()
    };
}
