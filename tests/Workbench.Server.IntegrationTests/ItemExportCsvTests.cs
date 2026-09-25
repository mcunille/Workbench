// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using static Workbench.Server.IntegrationTests.ItemExportEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemExportCsvTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task CsvPreservesTextThroughDocumentedDecodingAndUsesStableMetadata()
    {
        // GIVEN distinct persisted fields with punctuation, Unicode, newlines, controls and formula-like text.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client);
        var values = new[] {
            (Name: "Stone, \"蓝\"", Notes: "=1+2\r\nNext\tline", Location: "Tray A"),
            (Name: "'existing", Notes: "\u0001=1", Location: "@Display 蓝"),
        };
        var expected = new Dictionary<Guid, (string Name, string Notes, string Location)>();
        foreach (var value in values)
        {
            var created = await PostAsync(client, "/api/beta/items", new { creationRequestId = Guid.NewGuid(), name = value.Name, notes = value.Notes, location = value.Location });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var item = await created.Content.ReadFromJsonAsync<JsonElement>();
            expected.Add(item.GetProperty("id").GetGuid(), value);
        }
        // WHEN parsing the complete export with a CSV reader, not by splitting lines or commas.
        var response = await PostAsync(client, "/api/beta/items/export", new { scope = "active" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var parser = new TextFieldParser(stream, Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        // THEN every field is associated with its row and exactly one safety prefix is reversible.
        Assert.Equal("schema_version,exported_at_utc,scope,item_id,tracking_kind,name,notes,location,is_archived,created_at_utc,archived_at_utc,acquisition_id,acquisition_method,acquisition_source,acquisition_date_precision,acquisition_year,acquisition_month,acquisition_day,acquisition_notes".Split(','), parser.ReadFields());
        var exportedAt = new HashSet<string>();
        var seen = new HashSet<Guid>();
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields()!;
            Assert.Equal(19, row.Length);
            Assert.Equal("2", row[0]);
            exportedAt.Add(row[1]);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$", row[1]);
            Assert.Equal("active", row[2]);
            Assert.True(Guid.TryParseExact(row[3], "D", out var id));
            Assert.True(seen.Add(id));
            var original = expected[id];
            Assert.Equal("Individual", row[4]);
            Assert.Equal("'" + original.Name, row[5]);
            Assert.Equal("'" + original.Notes, row[6]);
            Assert.Equal("'" + original.Location, row[7]);
            Assert.Equal("false", row[8]);
            Assert.EndsWith("Z", row[9]);
            Assert.Equal("", row[10]);
        }
        Assert.Single(exportedAt);
        Assert.Equal(expected.Keys.Order(), seen.Order());
    }
}
