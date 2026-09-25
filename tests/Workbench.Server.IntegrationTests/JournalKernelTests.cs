// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalKernelTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task InternalKernelRequiresAnOuterTransaction()
    {
        // GIVEN a migrated disposable database and an authorized actor.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        await using var admin = await OpenPrivilegedTenantAsync(context);
        await using var command = NewKernelCommand(context, admin, null, ValidLines(context));
        // WHEN a privileged caller invokes the internal kernel without a source transaction.
        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        // THEN the kernel refuses a stand-alone partial financial command.
        Assert.Equal(51000, error.Number);
        Assert.Equal(0, await context.CountAsync("SourceEvents"));
    }

    [Fact]
    public async Task SqlBoundaryRejectsMalformedUnbalancedAndOverprecisionLines()
    {
        // GIVEN a configured tenant and two current General accounts.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        await using var admin = await OpenPrivilegedTenantAsync(context);
        var valid = ValidLines(context);
        var malformed = new[]
        {
            valid.Replace("\"debit\":\"12.34\"", "\"debit\":\"12.35\"", StringComparison.Ordinal),
            valid.Replace("\"credit\":\"0\"", "\"credit\":\"1\"", StringComparison.Ordinal),
            valid.Replace("12.34", "0", StringComparison.Ordinal),
            valid.Replace("12.34", "12.345", StringComparison.Ordinal),
            valid.Replace("12.34", "9999999999999999999999999", StringComparison.Ordinal),
            valid.Replace("12.34", "1e1", StringComparison.Ordinal),
            valid.Replace("12.34", "1,2", StringComparison.Ordinal),
            valid.Replace("12.34", "-12.34", StringComparison.Ordinal),
            valid.Replace("12.34", "12.34 ", StringComparison.Ordinal),
            valid.Replace("\"ordinal\":2", "\"ordinal\":1", StringComparison.Ordinal),
            valid.Replace("\"debit\":\"12.34\"", "\"debit\":\"12.34\",\"debit\":\"12.34\"", StringComparison.Ordinal),
            valid.Replace("\"credit\":\"0\"", "\"credit\":\"0\",\"extra\":1", StringComparison.Ordinal),
            valid.Replace(context.DebitAccountId.ToString(), context.DebitAccountId + "suffix", StringComparison.Ordinal)
        };
        foreach (var lines in malformed)
        {
            // WHEN the privileged adversarial caller submits malformed lines inside a transaction.
            await using var transaction = await admin.BeginTransactionAsync();
            await using var command = NewKernelCommand(context, admin, (SqlTransaction)transaction, lines);
            var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            // THEN the SQL boundary rejects each input and leaves no financial records.
            Assert.Equal(51000, error.Number);
        }
        Assert.Equal(0, await context.CountAsync("SourceEvents"));
        Assert.Equal(0, await context.CountAsync("PostingReceipts"));
    }

    [Fact]
    public async Task LineCountAndAggregateBoundsAreEnforcedBeforeNarrowingTotals()
    {
        // GIVEN exact active accounts and a four-decimal policy.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync(scale: 4);
        await using var admin = await OpenPrivilegedTenantAsync(context);
        string Many(int count, string amount) => JsonSerializer.Serialize(Enumerable.Range(1, count).Select(ordinal => new
        {
            ordinal,
            accountId = ordinal <= count / 2 ? context.DebitAccountId : context.CreditAccountId,
            accountVersion = ordinal <= count / 2 ? context.DebitAccountVersion : context.CreditAccountVersion,
            debit = ordinal <= count / 2 ? amount : "0",
            credit = ordinal <= count / 2 ? "0" : amount
        }));
        foreach (var lines in new[] { "[]", Many(1, "0.01"), Many(1001, "0.01"),
            Many(4, "999999999999999999999999.9999") })
        {
            // WHEN the command has too few, too many, or too much aggregate value.
            await using var rejected = (SqlTransaction)await admin.BeginTransactionAsync();
            await using var command = NewKernelCommand(context, admin, rejected, lines);
            // THEN the journal fails without a narrowed or partially written total.
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        // WHEN exactly 1000 small balanced lines are supplied.
        await using (var accepted = (SqlTransaction)await admin.BeginTransactionAsync())
        {
            await using var command = NewKernelCommand(context, admin, accepted, Many(1000, "0.01"));
            await using var reader = await command.ExecuteReaderAsync();
            // THEN the supported maximum line count posts as one balanced entry.
            Assert.True(await reader.ReadAsync());
            await reader.DisposeAsync();
            await accepted.CommitAsync();
        }
        Assert.Equal(1000, await context.CountAsync("JournalLines"));
        await using var totals = new SqlCommand("SELECT DebitTotal,CreditTotal FROM Accounting.JournalEntries WHERE TenantId=@tenant", context.Connection);
        totals.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        await using var posted = await totals.ExecuteReaderAsync();
        Assert.True(await posted.ReadAsync());
        Assert.Equal(5m, posted.GetDecimal(0));
        Assert.Equal(5m, posted.GetDecimal(1));
    }

    [Fact]
    public async Task SqlBoundaryRejectsCurrencyMismatchAndAllowsExactMaximumAmount()
    {
        // GIVEN a configured scale of four and active General accounts.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync(scale: 4);
        await using var admin = await OpenPrivilegedTenantAsync(context);
        var maximum = ValidLines(context, "999999999999999999999999.9999");
        // WHEN the source currency disagrees with the frozen configuration.
        await using (var transaction = (SqlTransaction)await admin.BeginTransactionAsync())
        {
            await using var command = NewKernelCommand(context, admin, transaction, maximum, "CAD");
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        // THEN the largest supported exact balanced entry can commit atomically.
        await using (var transaction = (SqlTransaction)await admin.BeginTransactionAsync())
        {
            await using var command = NewKernelCommand(context, admin, transaction, maximum);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            await reader.DisposeAsync();
            await transaction.CommitAsync();
        }
        Assert.Equal(1, await context.CountAsync("JournalEntries"));
        Assert.Equal(1, await context.CountAsync("PolicyFreezes"));
        await using var totals = new SqlCommand("SELECT DebitTotal,CreditTotal FROM Accounting.JournalEntries WHERE TenantId=@tenant", context.Connection);
        totals.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        await using var posted = await totals.ExecuteReaderAsync();
        Assert.True(await posted.ReadAsync());
        var expected = decimal.Parse("999999999999999999999999.9999", CultureInfo.InvariantCulture);
        Assert.Equal(expected, posted.GetDecimal(0));
        Assert.Equal(expected, posted.GetDecimal(1));
    }

    [Theory]
    [InlineData(0, "12.0000", "12.0001")]
    [InlineData(1, "12.3000", "12.3100")]
    [InlineData(2, "12.3400", "12.3410")]
    [InlineData(3, "12.3450", "12.3451")]
    [InlineData(4, "12.3456", null)]
    public async Task SelectedScalePreservesExactAmountsAndRejectsNonzeroExcess(int scale, string allowed, string? excess)
    {
        // GIVEN a tenant configured for one of the five supported posting scales.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync(scale);
        await using var admin = await OpenPrivilegedTenantAsync(context);
        if (excess is not null)
        {
            // WHEN a line contains nonzero digits beyond that scale.
            await using var rejected = (SqlTransaction)await admin.BeginTransactionAsync();
            await using var command = NewKernelCommand(context, admin, rejected, ValidLines(context, excess));
            // THEN SQL rejects the amount before decimal persistence can round it.
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        // WHEN the amount is exactly representable at the selected scale.
        await using (var accepted = (SqlTransaction)await admin.BeginTransactionAsync())
        {
            await using var command = NewKernelCommand(context, admin, accepted, ValidLines(context, allowed));
            await using var reader = await command.ExecuteReaderAsync();
            // THEN the journal is accepted with a source-owned exact amount.
            Assert.True(await reader.ReadAsync());
            await reader.DisposeAsync();
            await accepted.CommitAsync();
        }
        Assert.Equal(1, await context.CountAsync("JournalEntries"));
        await using var total = new SqlCommand("SELECT DebitTotal FROM Accounting.JournalEntries WHERE TenantId=@tenant", context.Connection);
        total.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        Assert.Equal(decimal.Parse(allowed, CultureInfo.InvariantCulture), (decimal)(await total.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PostingBoundaryUsesPostingDateAndPreservesEarlierDocumentAndEffectiveDates()
    {
        // GIVEN a planned start of January 15, with independent earlier document and effective dates.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync(startDate: "2026-01-15");
        await using var admin = await OpenPrivilegedTenantAsync(context);
        var documentDate = new DateTime(2025, 12, 31);
        var effectiveDate = new DateTime(2026, 1, 1);
        // WHEN the posting date is earlier than the configured start boundary.
        await using (var rejected = (SqlTransaction)await admin.BeginTransactionAsync())
        {
            await using var command = NewKernelCommand(context, admin, rejected, ValidLines(context),
                documentDate: documentDate, effectiveDate: effectiveDate, postingDate: new DateTime(2026, 1, 14));
            // THEN SQL rejects the posting and creates no journal or receipt.
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        Assert.Equal(0, await context.CountAsync("JournalEntries"));
        Assert.Equal(0, await context.CountAsync("PostingReceipts"));
        // WHEN the posting date reaches the boundary, while the other dates remain earlier.
        await using (var accepted = (SqlTransaction)await admin.BeginTransactionAsync())
        {
            await using var command = NewKernelCommand(context, admin, accepted, ValidLines(context),
                documentDate: documentDate, effectiveDate: effectiveDate, postingDate: new DateTime(2026, 1, 15));
            await using var result = await command.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            await result.DisposeAsync();
            await accepted.CommitAsync();
        }
        // THEN the source and journal retain each distinct date and one database-owned UTC recording instant.
        await using var evidence = new SqlCommand("""
            SELECT j.DocumentDate,j.EffectiveDate,j.PostingDate,j.RecordedAtUtc,
                   s.DocumentDate,s.EffectiveDate,s.PostingDate,s.RecordedAtUtc,
                   r.RecordedAtUtc,f.RecordedAtUtc,
                   SYSUTCDATETIME()
              FROM Accounting.JournalEntries j
              JOIN Accounting.SourceEvents s ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId
              JOIN Accounting.PostingReceipts r ON r.TenantId=j.TenantId AND r.JournalId=j.Id
              JOIN Accounting.PolicyFreezes f ON f.TenantId=j.TenantId AND f.FirstJournalId=j.Id
             WHERE j.TenantId=@tenant;
            """, context.Connection);
        evidence.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        await using var reader = await evidence.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(documentDate, reader.GetDateTime(0));
        Assert.Equal(effectiveDate, reader.GetDateTime(1));
        Assert.Equal(new DateTime(2026, 1, 15), reader.GetDateTime(2));
        var recorded = reader.GetDateTimeOffset(3);
        Assert.Equal(documentDate, reader.GetDateTime(4));
        Assert.Equal(effectiveDate, reader.GetDateTime(5));
        Assert.Equal(new DateTime(2026, 1, 15), reader.GetDateTime(6));
        Assert.Equal(recorded, reader.GetDateTimeOffset(7));
        Assert.Equal(recorded, reader.GetDateTimeOffset(8));
        Assert.Equal(recorded, reader.GetDateTimeOffset(9));
        Assert.Equal(TimeSpan.Zero, recorded.Offset);
        var databaseNow = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc));
        Assert.InRange(recorded, databaseNow.AddMinutes(-1), databaseNow);
    }

    [Fact]
    public async Task NewRequestCannotPostSameSourceEventByChangingRuleVersion()
    {
        // GIVEN one committed journal for a typed source identity and rule version one.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        await using var admin = await OpenPrivilegedTenantAsync(context);
        var sourceId = Guid.NewGuid();
        var sourceRevision = Guid.NewGuid();
        Guid existingSourceEventId;
        await using (var first = (SqlTransaction)await admin.BeginTransactionAsync())
        {
            await using var command = NewKernelCommand(context, admin, first, ValidLines(context),
                sourceId: sourceId, sourceRevision: sourceRevision, ruleVersion: 1);
            await using var result = await command.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            existingSourceEventId = result.GetGuid(0);
            await result.DisposeAsync();
            await first.CommitAsync();
        }
        // WHEN a distinct request retries that same source event with a changed rule version.
        await using (var second = (SqlTransaction)await admin.BeginTransactionAsync())
        {
            await using var command = NewKernelCommand(context, admin, second, ValidLines(context),
                sourceId: sourceId, sourceRevision: sourceRevision, ruleVersion: 2);
            // THEN the source identity conflicts; a rule change cannot bypass one-event uniqueness.
            var conflict = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(51009, conflict.Number);
            Assert.Contains(existingSourceEventId.ToString("D"), conflict.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(1, await context.CountAsync("SourceEvents"));
        Assert.Equal(1, await context.CountAsync("JournalEntries"));
        Assert.Equal(1, await context.CountAsync("PostingReceipts"));
    }

    [Fact]
    public async Task PostedPolicyFieldsCannotBeChangedOrRemovedAndUsedAccountCannotBeArchived()
    {
        // GIVEN a committed synthetic posting that establishes a policy freeze and uses two accounts.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        var source = await context.CreateSourceAsync();
        await context.PostAsync(source);
        var baseline = new Dictionary<string, object>
        {
            ["country"] = "US",
            ["region"] = "CA",
            ["currency"] = "USD",
            ["scale"] = 2,
            ["fiscalStartMonth"] = 1,
            ["startApproach"] = "OpeningBalances",
            ["plannedStartDate"] = "2026-01-01"
        };
        foreach (var field in new[] { "currency", "scale", "fiscalStartMonth", "startApproach", "plannedStartDate" })
        {
            var removed = new Dictionary<string, object>(baseline);
            removed.Remove(field);
            var nulled = new Dictionary<string, object>(baseline) { [field] = null! };
            var changed = new Dictionary<string, object>(baseline)
            {
                [field] = field switch
                {
                    "currency" => "CAD",
                    "scale" => 3,
                    "fiscalStartMonth" => 2,
                    "startApproach" => "FromBeginning",
                    _ => "2026-01-02"
                }
            };
            foreach (var policies in new[] { removed, nulled, changed })
            {
                // WHEN a frozen policy value is omitted or edited after first posting.
                var payload = JsonSerializer.Serialize(new { policies, mappings = Array.Empty<object>(), coverage = Array.Empty<object>() });
                var error = await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), "Configure", payload,
                    expectedVersion: context.ConfigurationVersion));
                // THEN Accounting.Save rejects the edit even though ordinary configuration validation permits the shape.
                Assert.Equal(50909, error.Number);
            }
        }
        // WHEN archiving an account referenced by an immutable journal line.
        var archive = await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), "ArchiveAccount",
            """{"isArchived":true}""", context.DebitAccountId, context.DebitAccountVersion));
        // THEN historical account identity stays available for readback.
        Assert.Equal(50909, archive.Number);
        Assert.Equal(1, await context.CountAsync("JournalEntries"));
    }

    private static string ValidLines(JournalTestContext context, string amount = "12.34") => JsonSerializer.Serialize(new[]
    {
        new { ordinal = 1, accountId = context.DebitAccountId, accountVersion = context.DebitAccountVersion, debit = amount, credit = "0" },
        new { ordinal = 2, accountId = context.CreditAccountId, accountVersion = context.CreditAccountVersion, debit = "0", credit = amount }
    });

    private static async Task<SqlConnection> OpenPrivilegedTenantAsync(JournalTestContext context)
    {
        var connection = new SqlConnection(context.Application.AdminConnectionString);
        await connection.OpenAsync();
        await new TenantContextProof(context.ProofKey).ApplyAsync(connection, JournalTestContext.TenantId, CancellationToken.None);
        return connection;
    }

    private static SqlCommand NewKernelCommand(JournalTestContext context, SqlConnection connection, SqlTransaction? transaction,
        string lines, string currency = "USD", DateTime? documentDate = null, DateTime? effectiveDate = null,
        DateTime? postingDate = null, Guid? sourceId = null, Guid? sourceRevision = null, int ruleVersion = 1)
    {
        var command = new SqlCommand("""
            EXEC Accounting.PostJournal @ActorId=@actor,@SessionId=@session,@RequestId=@request,
                @RequiredPermission=N'AccountingConfigurationManage',@SourceCommandKind=N'KernelTest',@SourceCommandVersion=1,
                @CanonicalInput=N'{}',@SourceKind=N'KernelTest',@SourceId=@source,@SourceRevision=@revision,
                @EventKind=N'Posted',@RuleVersion=@ruleVersion,@ExpectedConfigurationVersion=@config,@Currency=@currency,
                @DocumentDate=@documentDate,@EffectiveDate=@effectiveDate,@PostingDate=@postingDate,
                @Reference=NULL,@Reason=NULL,@SourceSnapshot=N'{}',@Lines=@lines;
            """, connection, transaction);
        command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@session", context.SessionId);
        command.Parameters.AddWithValue("@request", Guid.NewGuid());
        command.Parameters.AddWithValue("@source", sourceId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("@revision", sourceRevision ?? Guid.NewGuid());
        command.Parameters.AddWithValue("@ruleVersion", ruleVersion);
        command.Parameters.AddWithValue("@config", context.ConfigurationVersion);
        command.Parameters.AddWithValue("@currency", currency);
        command.Parameters.Add(new SqlParameter("@documentDate", System.Data.SqlDbType.Date) { Value = documentDate ?? new DateTime(2026, 2, 1) });
        command.Parameters.Add(new SqlParameter("@effectiveDate", System.Data.SqlDbType.Date) { Value = effectiveDate ?? new DateTime(2026, 2, 1) });
        command.Parameters.Add(new SqlParameter("@postingDate", System.Data.SqlDbType.Date) { Value = postingDate ?? new DateTime(2026, 2, 1) });
        command.Parameters.AddWithValue("@lines", lines);
        return command;
    }
}
