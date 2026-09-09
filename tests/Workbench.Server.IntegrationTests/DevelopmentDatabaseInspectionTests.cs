// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Administration;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class DevelopmentDatabaseInspectionTests
{
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
