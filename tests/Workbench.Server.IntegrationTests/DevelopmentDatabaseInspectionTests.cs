// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Administration;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class DevelopmentDatabaseInspectionTests
{
    [Theory]
    [InlineData("base,old,current", true)]
    [InlineData("base,old", false)]
    [InlineData("base,old,current,future", false)]
    [InlineData("old,current", false)]
    [InlineData("base,current,old", false)]
    [InlineData("base,old,old,current", false)]
    public void OnlyTheCompletedHistoricalPo03SequenceIsCompatible(string history, bool expected)
    {
        // GIVEN the consolidated release and a retained development history.
        const string old = "20260916183834_AddStructuredDraftOrderLines";
        const string current = "20260917010000_AddSupplierBasedDraftPricing";
        string[] known = ["base", current];
        var applied = history.Split(',').Select(id => id == "old" ? old : id == "current" ? current : id).ToArray();
        // WHEN inspecting without rewriting history THEN only the complete known sequence is accepted.
        Assert.Equal(expected, DevelopmentDatabaseInspection.IsCompatibleHistory(known, applied));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("001_A", true)]
    [InlineData("001_A,002_B", true)]
    [InlineData("001_A,002_B,003_C", false)]
    [InlineData("001_A,002_Other", false)]
    [InlineData("002_B", false)]
    [InlineData("002_B,001_A", false)]
    public void OnlyAnExactPrefixCanBeMigrated(string history, bool expected)
    {
        // GIVEN the image's ordered migrations and a retained database history.
        string[] known = ["001_A", "002_B"];
        var applied = history.Split(',', StringSplitOptions.RemoveEmptyEntries);

        // WHEN compatibility is checked before any migration runs.
        var compatible = DevelopmentDatabaseInspection.IsCompatibleHistory(known, applied);

        // THEN only an exact prefix is safe, including a fresh empty database.
        Assert.Equal(expected, compatible);
    }
}
