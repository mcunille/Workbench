// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidencePolicyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("2026-09-01", "2026-09-28T18:00:00Z", 7, "2033-09-28T18:00:00Z")]
    [InlineData("2027-01-01", "2026-09-28T18:00:00Z", 7, "2034-01-01T00:00:00Z")]
    [InlineData("2024-02-01", "2024-02-29T12:00:00Z", 1, "2025-02-28T12:00:00Z")]
    [InlineData("2026-09-01", "2026-09-28T18:00:00Z", null, null)]
    [InlineData("9999-01-01", "2026-09-28T18:00:00Z", 7, null)]
    public async Task DeadlineUsesLaterAnchorAndCalendarYears(string postingDate, string recordedAt, int? years, string? expected)
    {
        // GIVEN independently chosen exact boundary instants, including indefinite and unrepresentable policies.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var connection = new SqlConnection(database.AdminConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT Accounting.FinancialRetentionDeadline(@posting,@recorded,@years)", connection);
        command.Parameters.AddWithValue("@posting", DateTime.Parse(postingDate));
        command.Parameters.AddWithValue("@recorded", DateTimeOffset.Parse(recordedAt));
        command.Parameters.AddWithValue("@years", (object?)years ?? DBNull.Value);
        // WHEN the production SQL helper calculates the deadline THEN calendar clamping and UTC anchoring are exact.
        var result = await command.ExecuteScalarAsync();
        Assert.Equal(expected is null ? DBNull.Value : (object)DateTimeOffset.Parse(expected), result);
    }
}
