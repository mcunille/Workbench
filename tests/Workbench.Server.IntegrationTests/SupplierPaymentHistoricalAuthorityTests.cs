// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierPaymentHistoricalAuthorityTests(SupplierEvidenceScenarios scenarios) : IClassFixture<SupplierEvidenceScenarios>
{
    [Fact]
    public async Task EqualDebtAttributionRelinkCannotGrantHistoricalPaymentAuthority()
    {
        // GIVEN an independent copy of two equal bills and a real payment settling distinct payable-account versions.
        await using var prepared = await scenarios.OpenAsync("equalDebtAttribution");
        var context = prepared.Context;
        var first = prepared.Data["first"]!.GetValue<string>();
        var second = prepared.Data["second"]!.GetValue<string>();
        var group = prepared.Data["group"]!.GetValue<string>();
        var query = $"SELECT COUNT(*) FROM Purchasing.SupplierItemControl('{JournalTestContext.TenantId}','{prepared.Data["payment"]}')";
        Assert.Equal(1, await context.Bills.ScalarAsync<int>(query));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(DISTINCT Ordinal) FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}'"));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(DISTINCT AccountVersion) FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}'"));
        var firstMovement = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}' AND ItemId='{first}' AND EventKind='Apply'");
        var secondMovement = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}' AND ItemId='{second}' AND EventKind='Apply'");
        var secondAttribution = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}' AND MovementId='{secondMovement}'");
        try
        {
            // WHEN only the second attribution is relinked to the first equal movement, both line totals still agree.
            await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierControlAttributions SET MovementId='{firstMovement}' WHERE Id='{secondAttribution}'");
            // THEN incomplete per-movement coverage cannot grant historical account authority.
            Assert.Equal(0, await context.Bills.ScalarAsync<int>(query));
        }
        finally
        {
            await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierControlAttributions SET MovementId='{secondMovement}' WHERE Id='{secondAttribution}'");
        }
        // AND restoring the one changed link restores the original valid proof.
        Assert.Equal(1, await context.Bills.ScalarAsync<int>(query));
    }
}
