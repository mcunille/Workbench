// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed partial class JournalCorrectionSecurityTests
{
    [Fact]
    [Trait("Category", "MutationProbe")]
    public async Task ExactInverseAssertionDetectsUnswappedSqlAmounts()
    {
        // GIVEN an inverse assertion that passes for the real restricted correction kernel.
        await using var controls = await JournalControlTestContext.OpenAsync(Fixture);
        var journal = controls.Journal;
        var original = await journal.PostAsync(await journal.CreateSourceAsync("300"));
        var correct = await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), null);
        await CorrectionAssertions.ExactInverseAsync(controls, original.JournalId, correct.ReversalJournalId);
        // WHEN the disposable shared inverse projection stops swapping the amounts.
        await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        const string readInverse = "SELECT OBJECT_DEFINITION(OBJECT_ID(N'Accounting.JournalInverseLines'))";
        var installed = (string)(await CorrectionAssertions.ScalarAsync(admin, readInverse))!;
        var mutated = installed.Replace("Credit Debit,Debit Credit", "Debit Debit,Credit Credit", StringComparison.Ordinal);
        Assert.NotEqual(installed, mutated);
        try
        {
            await InstallInverseAsync(mutated);
            var second = await journal.PostAsync(await journal.CreateSourceAsync("300"));
            var mutant = await controls.CorrectAsync(second.JournalId, new DateOnly(2026, 10, 1), null);
            var assertion = await Record.ExceptionAsync(() => CorrectionAssertions.ExactInverseAsync(controls, second.JournalId, mutant.ReversalJournalId));
            // THEN the unchanged behavioral assertion kills the mutant.
            Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(assertion);
        }
        finally { await InstallInverseAsync(installed); }
        var restored = await journal.PostAsync(await journal.CreateSourceAsync("300"));
        var restoredCorrection = await controls.CorrectAsync(restored.JournalId, new DateOnly(2026, 10, 1), null);
        await CorrectionAssertions.ExactInverseAsync(controls, restored.JournalId, restoredCorrection.ReversalJournalId);

        async Task InstallInverseAsync(string definition)
        {
            static string AsAlter(string sql) => sql.Replace("CREATE FUNCTION", "ALTER FUNCTION", StringComparison.Ordinal);
            var definitionToInstall = AsAlter(definition);
            await using var command = new SqlCommand(definitionToInstall, admin);
            await command.ExecuteNonQueryAsync();
            var actual = (string)(await CorrectionAssertions.ScalarAsync(admin, readInverse))!;
            Assert.Equal(definitionToInstall, AsAlter(actual));
        }
    }
}
