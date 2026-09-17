// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Administration;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class DevelopmentDatabaseInspectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterKnownMigrationsPreserveRetainedBranchUpgradeSupport(bool betaBranch)
    {
        // GIVEN an application that adds a migration after the documented integration boundary.
        string[] known = ["base", "20260917010000_AddSupplierBasedDraftPricing",
            "20260917015000_PrepareRetainedBetaFinancialUpgrade", "20260917020000_AddDraftFinancialAdjustments",
            "20260917030000_ProtectConfirmedSupplierChargeCorrections", "20260917080000_ConsolidateBetaDraftCommands",
            "20260918010000_RemoveHistoricalDraftReplay", "20260918020000_IntegrateBetaDraftFinancialAdjustments", "future"];
        var retained = known.Take(2).Concat(betaBranch ? known.Skip(5).Take(2) : known.Skip(3).Take(2)).ToArray();
        // WHEN inspecting an old branch THEN pending later migrations do not invalidate its supported upgrade.
        Assert.True(DevelopmentDatabaseInspection.IsCompatibleHistory(known, retained));
        // AND a history that skipped the integration to apply that later migration remains divergent.
        Assert.False(DevelopmentDatabaseInspection.IsCompatibleHistory(known, retained.Append("future").ToArray()));
    }

    [Theory]
    [InlineData("base,po03,f1", true)]
    [InlineData("base,po03,f1,f2", true)]
    [InlineData("base,po03,b1", true)]
    [InlineData("base,po03,b1,b2", true)]
    [InlineData("base,po03,prep,b1", true)]
    [InlineData("base,po03,prep,b1,b2", true)]
    [InlineData("base,po03,prep,f1,b1", true)]
    [InlineData("base,po03,prep,f1,b1,b2", true)]
    [InlineData("base,po03,prep,f1,f2,b1,b2,final", true)]
    [InlineData("base,old,po03,b1,b2", true)]
    [InlineData("base,old,po03,f1,f2", true)]
    [InlineData("base,old,po03,prep,f1,b1,b2", true)]
    [InlineData("base,po03,f2", false)]
    [InlineData("base,po03,b2", false)]
    [InlineData("base,po03,f1,b1,b2", false)]
    [InlineData("base,po03,prep,f2,b1,b2", false)]
    [InlineData("base,po03,b1,b2,final", false)]
    [InlineData("base,po03,b1,prep", false)]
    [InlineData("po03,b1,b2", false)]
    [InlineData("base,b1,b2", false)]
    [InlineData("base,old,b1,b2", false)]
    [InlineData("base,po03,prep,f1,f2,b1,b2,final,future", false)]
    [InlineData("base,po03,b1,b1", false)]
    public void OnlyDocumentedBranchHistoriesAndOrderedForwardProgressAreCompatible(string history, bool expected)
    {
        // GIVEN the merged migration order and one retained or interrupted branch history.
        var ids = new Dictionary<string, string>
        {
            ["old"] = "20260916183834_AddStructuredDraftOrderLines",
            ["po03"] = "20260917010000_AddSupplierBasedDraftPricing",
            ["prep"] = "20260917015000_PrepareRetainedBetaFinancialUpgrade",
            ["f1"] = "20260917020000_AddDraftFinancialAdjustments",
            ["f2"] = "20260917030000_ProtectConfirmedSupplierChargeCorrections",
            ["b1"] = "20260917080000_ConsolidateBetaDraftCommands",
            ["b2"] = "20260918010000_RemoveHistoricalDraftReplay",
            ["final"] = "20260918020000_IntegrateBetaDraftFinancialAdjustments"
        };
        string[] Expand(string value) => value.Split(',').Select(id => ids.GetValueOrDefault(id, id)).ToArray();
        var known = Expand("base,po03,prep,f1,f2,b1,b2,final");
        // WHEN inspection evaluates the complete applied history THEN it accepts only explicit safe lineages.
        Assert.Equal(expected, DevelopmentDatabaseInspection.IsCompatibleHistory(known, Expand(history)));
    }

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
