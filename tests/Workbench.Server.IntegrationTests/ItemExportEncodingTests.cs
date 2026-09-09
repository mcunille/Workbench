// Copyright (c) 2026 The White Stag Collection.

using System.Text;
using Microsoft.VisualBasic.FileIO;
using Workbench.Server.Inventory;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class ItemExportEncodingTests
{
    [Theory]
    [InlineData("=1+2")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(1)")]
    [InlineData("'original")]
    [InlineData(" \t=SUM(1)")]
    [InlineData("\u0001=1")]
    [InlineData("蓝, \"quoted\"\r\nnext")]
    public void TextEncodingIsReversibleAndIndependentOfSpreadsheetLeadingCharacters(string text)
    {
        // GIVEN literal user text and an absent optional field.
        var created = new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.FromHours(2));
        var item = new ExportItem(Guid.NewGuid(), "Individual", text, text, null, created, created.AddHours(1));
        // WHEN the CSV is parsed with a standard parser and one apostrophe prefix is removed.
        var encoded = ItemExportCsv.Encode([item], "all", created, CancellationToken.None);
        using var stream = new MemoryStream(encoded);
        using var csv = new TextFieldParser(stream, Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        csv.SetDelimiters(",");
        csv.ReadFields();
        var row = csv.ReadFields()!;
        // THEN the original text round-trips exactly, absent is empty, and timestamps are UTC.
        Assert.Equal("'" + text, row[5]);
        Assert.Equal(text, row[6][1..]);
        Assert.Equal("", row[7]);
        Assert.Equal("true", row[8]);
        Assert.Equal("2026-09-08T05:00:00.0000000Z", row[9]);
        Assert.Equal("2026-09-08T06:00:00.0000000Z", row[10]);
        Assert.True(csv.EndOfData);
    }
}
