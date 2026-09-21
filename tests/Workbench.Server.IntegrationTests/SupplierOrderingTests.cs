// Copyright (c) 2026 The White Stag Collection.
using System.Data.SqlTypes;
using System.Net;
using System.Net.Http.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

public sealed partial class PurchasingIdentityEndpointTests
{
    [Fact]
    public async Task SupplierCursorPreservesMaximumUnicodeNamesAndRejectsMalformedContinuations()
    {
        // GIVEN a full page boundary inside identical names at the supported UTF-16 length limit.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var name = string.Concat(Enumerable.Repeat("名💎_", 50));
        var ids = new List<Guid>();
        for (var index = 0; index < 51; index++)
            ids.Add((await SupplierSave(client, Contact with { Name = name })).SupplierId);

        // WHEN continuing the directory THEN the complete Unicode key survives and no equal name is lost.
        var first = (await client.GetFromJsonAsync<SupplierPageResponse>("/api/beta/suppliers"))!;
        Assert.Equal(50, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = (await client.GetFromJsonAsync<SupplierPageResponse>("/api/beta/suppliers?cursor=" + Uri.EscapeDataString(first.NextCursor)))!;
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
        Assert.Equal(ids.OrderBy(id => new SqlGuid(id)), first.Items.Concat(second.Items).Select(row => row.Id));

        // AND malformed, oversized, empty-key, and old timestamp continuations request a refresh.
        var parts = first.NextCursor.Split('_');
        var suffix = "_" + parts[2] + "_" + parts[3];
        foreach (var invalid in new[] { "garbage", new string('x', 641), "sn1_!" + suffix,
            "sn1_" + suffix, "sn1_AA==" + suffix,
            "sn1_" + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(new string('a', 201))) + suffix,
            "sn1_" + parts[1] + "_" + Guid.Empty.ToString("N") + "_" + parts[3],
            "v2_" + parts[1] + suffix })
            await AssertInvalidIdentityCursor(client, "/api/beta/suppliers?cursor=" + Uri.EscapeDataString(invalid));
    }

    [Fact]
    public async Task SupplierAlphabeticalOrderingSpansPagesFiltersAndEdits()
    {
        // GIVEN mixed-case duplicate names spanning a page boundary, in nonalphabetical creation order.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var saved = new List<(Guid Id, string Name)>();
        foreach (var name in new[] { "Zulu match", "alpha match", "Beta match" }
            .Concat(Enumerable.Range(0, 52).Select(index => index % 2 == 0 ? "Middle match" : "middle MATCH")))
            saved.Add(((await SupplierSave(client, Contact with { Name = name })).SupplierId, name));
        var archived = await SupplierSave(client, Contact with { Name = "Aardvark match" });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post,
            $"/api/beta/suppliers/{archived.SupplierId}/archive",
            new ArchiveSupplierRequest(Guid.NewGuid(), archived.SavedVersion, true))).StatusCode);
        await SupplierSave(client, Contact with { Name = "Unrelated" });

        async Task<SupplierResponse[]> Browse(string filter)
        {
            var rows = new List<SupplierResponse>();
            string? cursor = null;
            do
            {
                var page = (await client.GetFromJsonAsync<SupplierPageResponse>("/api/beta/suppliers?" + filter
                    + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))))!;
                Assert.InRange(page.Items.Count, 1, 50);
                rows.AddRange(page.Items);
                Assert.Equal(rows.Count, rows.Select(row => row.Id).Distinct().Count());
                Assert.True(rows.Count <= 57);
                cursor = page.NextCursor;
            } while (cursor is not null);
            return rows.ToArray();
        }
        Guid[] Expected(IEnumerable<(Guid Id, string Name)> rows) => rows
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => new SqlGuid(row.Id)).Select(row => row.Id).ToArray();

        // WHEN searching across pages THEN equal names use SQL GUID order and every match occurs once.
        Assert.Equal(Expected(saved), (await Browse("query=match")).Select(row => row.Id));
        // AND archive filtering and unfiltered directory/picker requests retain the same order.
        Assert.Equal(Expected(saved.Append((archived.SupplierId, "Aardvark match"))),
            (await Browse("query=MATCH&includeArchived=true")).Select(row => row.Id));
        var before = await Browse("");
        Assert.Equal(Expected(before.Select(row => (row.Id, row.Supplier.Name))), before.Select(row => row.Id));

        // WHEN only contact details change THEN a refreshed directory keeps every supplier in place.
        var target = before[0];
        var edit = await SendAsync(client, HttpMethod.Put, $"/api/beta/suppliers/{target.Id}",
            new UpdateSupplierRequest(Guid.NewGuid(), target.Version, target.Supplier with { Phone = "555 1234" }));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(before.Select(row => row.Id), (await Browse("")).Select(row => row.Id));

        // WHEN that supplier is renamed THEN refreshing places it at its new alphabetical position.
        var receipt = (await edit.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/api/beta/suppliers/{target.Id}",
            new UpdateSupplierRequest(Guid.NewGuid(), receipt.SavedVersion, target.Supplier with { Name = "zz last" }))).StatusCode);
        var renamed = await Browse("");
        Assert.Equal(target.Id, renamed[^1].Id);
        Assert.Equal(Expected(renamed.Select(row => (row.Id, row.Supplier.Name))), renamed.Select(row => row.Id));
    }
}
