// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Workbench.Server.Purchasing;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class HistoricalDraftFingerprintTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HistoricalCanonicalBytesRemainFrozen(int version)
    {
        // GIVEN a retained request containing normalization-sensitive text and decimal amounts.
        const string entryId = "11111111-1111-1111-1111-111111111111";
        var structured = version >= 3 ? ",\"quantity\":\"2\",\"unitOfMeasure\":\"piece\"" : "";
        var pricing = version == 3 ? ",\"unitPrice\":null,\"pricingUnit\":null,\"pricePerQuantity\":null,\"pricingQuantity\":null"
            : version == 4 ? ",\"priceMode\":\"perUnit\",\"price\":null,\"legacyPricing\":null" : "";
        var details = version >= 3 ? ",\"supplierSku\":\" SKU \",\"itemType\":null" : "";
        const string identity = ",\"supplierId\":null,\"supplierContactName\":null,\"supplierEmail\":null,\"supplierPhone\":null,\"supplierWebsite\":null,\"supplierPostalAddress\":null,\"supplierOrderReference\":\" REF \",\"platform\":null";
        var input = "{\"title\":\" Title \",\"supplierName\":null,\"currency\":\"usd\",\"notes\":\"<\",\"sourceLinks\":[],\"entries\":[{\"id\":\"" + entryId + "\",\"description\":null,\"notes\":null,\"sourceLink\":null,\"indicativePrice\":\"1.2\"" + structured + pricing + details + "}]" + (version >= 2 ? identity : "") + "}";
        using var json = JsonDocument.Parse(input);
        // WHEN recovered using the retired-route canonicalizer.
        var actual = RetiredDraftOrderReceipts.Canonical(version, "Create", null, null, json.RootElement);
        // THEN the UTF-16 fingerprint document preserves historical declaration order, escaping and number formatting.
        const string prefix = "{\"operation\":\"Create\",\"targetId\":null,\"expectedVersion\":null,\"draft\":{\"title\":\"Title\",\"supplierName\":null,\"currency\":\"USD\",\"notes\":\"\\u003C\",\"sourceLinks\":[],\"entries\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"description\":null,\"notes\":null,\"sourceLink\":null,\"indicativePrice\":\"1.2000\"";
        const string expectedIdentity = ",\"supplierId\":null,\"supplierContactName\":null,\"supplierEmail\":null,\"supplierPhone\":null,\"supplierWebsite\":null,\"supplierPostalAddress\":null,\"supplierOrderReference\":\"REF\",\"platform\":null";
        var expected = version switch
        {
            1 => prefix + "}]}}",
            2 => prefix + "}]" + expectedIdentity + "}}",
            3 => prefix + ",\"quantity\":\"2.0000\",\"unitOfMeasure\":\"piece\",\"unitPrice\":null,\"pricingUnit\":null,\"pricePerQuantity\":null,\"pricingQuantity\":null,\"supplierSku\":\"SKU\",\"itemType\":null}]" + expectedIdentity + "}}",
            _ => prefix + ",\"quantity\":\"2.0000\",\"unitOfMeasure\":\"piece\",\"priceMode\":\"perUnit\",\"price\":null,\"legacyPricing\":null,\"supplierSku\":\"SKU\",\"itemType\":null}]" + expectedIdentity + "}}"
        };
        Assert.Equal(expected, actual);
    }
}
