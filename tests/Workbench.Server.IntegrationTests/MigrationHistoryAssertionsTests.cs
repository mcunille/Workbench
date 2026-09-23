// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;
using Xunit.Sdk;

namespace Workbench.Server.IntegrationTests;

public sealed class MigrationHistoryAssertionsTests
{
    private const string RetainedMigration = "20260916183834_AddStructuredDraftOrderLines";

    [Fact]
    public void AcceptsCurrentHistory()
    {
        // GIVEN exactly the current release manifest.
        var applied = CurrentSchema.Migrations.ToArray();

        // WHEN the history is checked THEN every expected migration is accepted in order.
        MigrationHistoryAssertions.AssertCurrent(applied);
    }

    [Fact]
    public void AcceptsExplicitRetainedHistoryInItsOrderedPosition()
    {
        // GIVEN current migrations and an explicitly retained preview migration.
        var applied = CurrentSchema.Migrations.Append(RetainedMigration).Order(StringComparer.Ordinal);

        // WHEN checked with that exception THEN the complete ordered history is accepted.
        MigrationHistoryAssertions.AssertCurrent(applied, RetainedMigration);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("reordered")]
    [InlineData("divergent")]
    [InlineData("extra")]
    [InlineData("case")]
    [InlineData("duplicate")]
    public void RejectsHistoryThatDoesNotMatchTheCurrentManifest(string difference)
    {
        // GIVEN a history with an omitted, reordered, changed, extra, or duplicate migration.
        var applied = CurrentSchema.Migrations.ToList();
        switch (difference)
        {
            case "missing": applied.RemoveAt(applied.Count - 1); break;
            case "reordered": (applied[0], applied[1]) = (applied[1], applied[0]); break;
            case "divergent": applied[^1] = "99999999999999_DivergentMigration"; break;
            case "extra": applied.Add("99999999999999_UnexpectedMigration"); break;
            case "case": applied[^1] = applied[^1].ToUpperInvariant(); break;
            case "duplicate": applied.Add(applied[^1]); break;
        }

        // WHEN checked THEN exact ordinal sequence comparison rejects the discrepancy.
        Assert.ThrowsAny<XunitException>(() => MigrationHistoryAssertions.AssertCurrent(applied));
    }

    [Fact]
    public void RejectsUndeclaredRetainedHistory()
    {
        // GIVEN a preview migration that was not declared as retained by this test.
        var applied = CurrentSchema.Migrations.Append(RetainedMigration).Order(StringComparer.Ordinal);

        // WHEN checked THEN extra history cannot silently pass as current.
        Assert.ThrowsAny<XunitException>(() => MigrationHistoryAssertions.AssertCurrent(applied));
    }

    [Fact]
    public void RequiresDeclaredRetainedHistoryToBePresent()
    {
        // GIVEN current history without the preview migration the caller expects to retain.
        var applied = CurrentSchema.Migrations;

        // WHEN checked THEN the missing retained entry is rejected.
        Assert.ThrowsAny<XunitException>(() => MigrationHistoryAssertions.AssertCurrent(applied, RetainedMigration));
    }
}
