// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Inventory;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class AcquisitionInputTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(1, null, null)]
    [InlineData(2024, 2, 29)]
    [InlineData(2000, 2, 29)]
    [InlineData(2026, null, null)]
    [InlineData(2026, 9, null)]
    [InlineData(2026, 9, 9)]
    public void KnownPrecisionAndLeapDatesAreAccepted(int? year, int? month, int? day)
    {
        // GIVEN a fixed UTC calendar day and each supported method.
        foreach (var method in new[] { "Purchase", "Gift", "Inheritance", "Trade", "Other", "Unknown" })
        {
            var input = new CreateAcquisitionRequest(Guid.NewGuid(), null, method, null, year, month, day, null);
            // WHEN validating THEN neither missing optional facts nor valid precision invents an error.
            Assert.Empty(AcquisitionInput.Validate(input, new Clock()));
        }
    }

    [Theory]
    [InlineData(null, 1, null, "month")]
    [InlineData(null, null, 1, "day")]
    [InlineData(2024, null, 1, "day")]
    [InlineData(0, null, null, "year")]
    [InlineData(10000, null, null, "year")]
    [InlineData(2024, 0, null, "month")]
    [InlineData(2024, 13, null, "month")]
    [InlineData(2024, 2, 0, "day")]
    [InlineData(2024, 2, 30, "day")]
    [InlineData(1900, 2, 29, "day")]
    [InlineData(2023, 2, 29, "day")]
    [InlineData(2027, null, null, "year")]
    [InlineData(2026, 10, null, "month")]
    [InlineData(2026, 9, 10, "day")]
    public void IncompleteImpossibleAndFutureDatesHaveFieldErrors(int? year, int? month, int? day, string field)
    {
        // GIVEN incomplete, impossible or future facts at their stated precision.
        var input = new CreateAcquisitionRequest(Guid.NewGuid(), null, "Unknown", null, year, month, day, null);
        // WHEN validating THEN the offending field is identified.
        Assert.Contains(field, AcquisitionInput.Validate(input, new Clock()).Keys);
    }

    [Fact]
    public void NormalizationPreservesNotesAndEnforcesExactLengthsAndMethods()
    {
        // GIVEN meaningful note whitespace and source padding.
        var input = new CreateAcquisitionRequest(Guid.NewGuid(), null, "Gift", " \u2003Family\t", null, null, null, " notes  \n");
        // WHEN normalizing THEN only the source is trimmed and blank optional text becomes null.
        var normalized = AcquisitionInput.Normalize(input);
        Assert.Equal("Family", normalized.Source);
        Assert.Equal(input.Notes, normalized.Notes);
        Assert.Null(AcquisitionInput.Normalize(input with { Notes = " \t\u2003" }).Notes);
        Assert.Null(AcquisitionInput.Normalize(input with { Source = " \t\u2003" }).Source);
        Assert.Empty(AcquisitionInput.Validate(input with { Source = new string('s', 200), Notes = new string('n', 4000) }, new Clock()));
        Assert.Contains("source", AcquisitionInput.Validate(input with { Source = new string('s', 201) }, new Clock()).Keys);
        Assert.Contains("notes", AcquisitionInput.Validate(input with { Notes = new string('n', 4001) }, new Clock()).Keys);
        foreach (var method in new[] { null, "", "gift", "Gift ", "Loan" })
            Assert.Contains("method", AcquisitionInput.Validate(input with { Method = method }, new Clock()).Keys);
        Assert.Contains("creationRequestId", AcquisitionInput.Validate(input with { CreationRequestId = Guid.Empty }, new Clock()).Keys);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 9, 0, 1, 0, TimeSpan.Zero);
    }
}
