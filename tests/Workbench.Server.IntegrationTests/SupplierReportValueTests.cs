// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Numerics;
using Microsoft.AspNetCore.DataProtection;
using Workbench.Server.Accounting;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class SupplierReportValueTests
{
    [Theory]
    [InlineData("0", 2, "0", "0.00")]
    [InlineData("1234", 0, "12340000", "1234")]
    [InlineData("-1234", 0, "-12340000", "-1234")]
    [InlineData("1.2345", 4, "12345", "1.2345")]
    [InlineData("-1.2345", 4, "-12345", "-1.2345")]
    [InlineData("123456789012345678901234567890", 2, "1234567890123456789012345678900000", "123456789012345678901234567890.00")]
    public async Task FinancialReportMoneyPreservesExactWideUnits(string amount, int scale, string expectedUnits, string expectedDisplay)
    {
        await Task.CompletedTask;
        // GIVEN exact monetary evidence, including whole, negative and beyond-CLR-decimal values.
        var units = BigInteger.Parse(expectedUnits, CultureInfo.InvariantCulture);
        // WHEN it is converted to report units and rendered at its frozen currency scale.
        Assert.Equal(units, SupplierReconciliationQueries.Units(amount));
        // THEN its mathematical value, sign and requested precision are retained without rounding.
        Assert.Equal(expectedDisplay, SupplierReconciliationQueries.Money(units, scale));
    }

    [Theory]
    [InlineData("0", -1)]
    [InlineData("0", 5)]
    [InlineData("12345", 2)]
    [InlineData("-12345", 0)]
    public async Task FinancialReportMoneyRefusesUnrepresentableEvidence(string units, int scale)
    {
        await Task.CompletedTask;
        // GIVEN an unsupported scale or evidence with a nonzero fraction beyond the frozen scale.
        var exact = BigInteger.Parse(units, CultureInfo.InvariantCulture);
        // WHEN rendered THEN rejection prevents a plausible-looking silently rounded financial amount.
        Assert.Throws<InvalidOperationException>(() => SupplierReconciliationQueries.Money(exact, scale));
    }

    [Fact]
    public async Task RequestCancellationDuringReportOrderingPropagatesTheRequestToken()
    {
        await Task.CompletedTask;
        // GIVEN a report page whose request is cancelled while ordering keys are being prepared.
        using var request = new CancellationTokenSource();
        var filter = new SupplierReportFilters(Guid.NewGuid(), "items", null, null, null, DateOnly.MaxValue,
            DateTimeOffset.UnixEpoch, 2, 0, 0);
        var snapshot = new SupplierReconciliationQueries.Snapshot(filter, request.Token);
        string Key(int row)
        {
            if (row == 2) request.Cancel();
            return row.ToString(CultureInfo.InvariantCulture);
        }
        // WHEN the normal paging algorithm observes that cancellation THEN it retains the original request cancellation.
        var error = Assert.Throws<OperationCanceledException>(() => snapshot.Page(new[] { 3, 1, 2 }, Key,
            _ => ("Advance", BigInteger.One), new EphemeralDataProtectionProvider()));
        Assert.Equal(request.Token, error.CancellationToken);
    }
}
