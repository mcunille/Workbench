// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Text;
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
        // GIVEN punctuation, Unicode, newlines and formula-like values in user fields.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client);
        string[] values = ["=1+2", "+cmd", "-1", "@SUM(1)", "'existing", "Stone, \"蓝\"\r\nNext", "a\t=1", "\u0001=1"];
        foreach (var value in values)
            Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, "/api/items", new { creationRequestId = Guid.NewGuid(), name = value, notes = value, location = value })).StatusCode);
        // WHEN parsing the complete export with a CSV reader, not by splitting lines or commas.
        var response = await PostAsync(client, "/api/items/export", new { scope = "active" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var parser = new TextFieldParser(stream, Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        // THEN every field is associated with its row and exactly one safety prefix is reversible.
        Assert.Equal("schema_version,exported_at_utc,scope,item_id,tracking_kind,name,notes,location,is_archived,created_at_utc,archived_at_utc".Split(','), parser.ReadFields());
        var exportedAt = new HashSet<string>();
        var decoded = new List<string>();
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields()!;
            Assert.Equal(11, row.Length);
            Assert.Equal("1", row[0]);
            exportedAt.Add(row[1]);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$", row[1]);
            Assert.Equal("active", row[2]);
            Assert.True(Guid.TryParseExact(row[3], "D", out _));
            Assert.Equal("Individual", row[4]);
            Assert.StartsWith("'", row[5]);
            Assert.Equal(row[5], row[6]);
            Assert.Equal(row[5], row[7]);
            decoded.Add(row[5][1..]);
            Assert.Equal("false", row[8]);
            Assert.EndsWith("Z", row[9]);
            Assert.Equal("", row[10]);
        }
        Assert.Single(exportedAt);
        Assert.Equal(values.Order(), decoded.Order());
    }
}
