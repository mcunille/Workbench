// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalReportEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task TrialBalanceAccumulatesBeyondClrDecimalWithoutRounding()
    {
        // GIVEN 80,000 privileged synthetic read-fixture entries, each below the persisted decimal(28,4) limit.
        // AND their combined activity exceeds the maximum CLR decimal by a narrow margin.
        await using var journal = await JournalTestContext.OpenAsync(sqlServer);
        await journal.CreateGeneralAccountsAsync();
        await journal.ConfigureAsync(scale: 4);
        var anchor = await journal.CreateSourceAsync();
        await journal.PostAsync(anchor);
        await using (var connection = new SqlConnection(journal.Application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new SqlCommand("""
                CREATE TABLE #JournalSeed(SourceEventId uniqueidentifier NOT NULL,JournalId uniqueidentifier NOT NULL,SourceId uniqueidentifier NOT NULL);
                INSERT #JournalSeed(SourceEventId,JournalId,SourceId)
                    SELECT TOP (80000) NEWID(),NEWID(),NEWID()
                    FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                DECLARE @now datetimeoffset=SYSUTCDATETIME();
                DECLARE @amount decimal(28,4)=CONVERT(decimal(28,4),'999999999999999999999999.9999');
                INSERT Accounting.SourceEvents(Id,TenantId,SourceKind,SourceId,SourceRevision,EventKind,RuleVersion,
                    ActorId,DocumentDate,EffectiveDate,PostingDate,Reference,Reason,SnapshotJson,SnapshotSha256,RecordedAtUtc)
                    SELECT s.SourceEventId,@tenant,N'Synthetic',s.SourceId,NEWID(),N'Historical',1,@actor,
                        '2026-02-01','2026-02-01','2026-02-01',NULL,NULL,N'{}',HASHBYTES('SHA2_256',N'{}'),@now
                    FROM #JournalSeed s;
                INSERT Accounting.JournalEntries(Id,TenantId,SourceEventId,ConfigurationVersion,Currency,Scale,
                    DocumentDate,EffectiveDate,PostingDate,RecordedAtUtc,ActorId,Reference,Reason,DebitTotal,CreditTotal)
                    SELECT s.JournalId,@tenant,s.SourceEventId,@configuration,N'USD',4,
                        '2026-02-01','2026-02-01','2026-02-01',@now,@actor,NULL,NULL,@amount,@amount
                    FROM #JournalSeed s;
                INSERT Accounting.JournalLines(TenantId,JournalId,Ordinal,AccountId,AccountVersion,
                    AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit)
                    SELECT @tenant,s.JournalId,v.Ordinal,v.AccountId,v.AccountVersion,v.Code,v.Name,v.Type,N'General',
                        CASE WHEN v.Ordinal=1 THEN @amount ELSE 0 END,
                        CASE WHEN v.Ordinal=2 THEN @amount ELSE 0 END
                    FROM #JournalSeed s CROSS APPLY (VALUES
                        (1,@debit,@debitVersion,N'1910',N'Synthetic debit',N'Asset'),
                        (2,@credit,@creditVersion,N'2910',N'Synthetic credit',N'Liability'))
                        v(Ordinal,AccountId,AccountVersion,Code,Name,Type);
                """, connection) { CommandTimeout = 180 };
            seed.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            seed.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
            seed.Parameters.AddWithValue("@configuration", journal.ConfigurationVersion);
            seed.Parameters.AddWithValue("@debit", journal.DebitAccountId);
            seed.Parameters.AddWithValue("@credit", journal.CreditAccountId);
            seed.Parameters.AddWithValue("@debitVersion", journal.DebitAccountVersion);
            seed.Parameters.AddWithValue("@creditVersion", journal.CreditAccountVersion);
            await seed.ExecuteNonQueryAsync();
        }
        using var client = journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);

        // WHEN reading the trial balance THEN SQL returns every digit as a string with four places.
        using var response = await client.GetAsync("/api/beta/accounting/trial-balance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var units = BigInteger.Parse("9999999999999999999999999999") * 80000 + 123400;
        var expected = $"{units / 10000}.{(units % 10000).ToString().PadLeft(4, '0')}";
        Assert.Equal(expected, body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
        Assert.Equal(expected, body.RootElement.GetProperty("wholeFilterTotals").GetProperty("credit").GetString());
        var rows = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, row => row.GetProperty("debitMinusCredit").GetString() == expected);
        Assert.Contains(rows, row => row.GetProperty("debitMinusCredit").GetString() == "-" + expected);
    }

    [Fact]
    public async Task ReportOverlappingBlockedPostingReturnsOneCoherentState()
    {
        // GIVEN one posted source and an independent SQL connection holding the tenant posting lock.
        await using var journal = await JournalTestContext.OpenAsync(sqlServer);
        await journal.CreateGeneralAccountsAsync();
        await journal.ConfigureAsync();
        await journal.PostAsync(await journal.CreateSourceAsync(amount: "2.00"));
        var pendingSource = await journal.CreateSourceAsync(amount: "5.00");
        using var client = journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);
        await using var blocker = await journal.OpenSiblingAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var acquire = new SqlCommand("""
            DECLARE @result int;
            DECLARE @resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@tenant);
            EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',
                @LockOwner='Transaction',@LockTimeout=10000;
            SELECT @result;
            """, blocker, transaction))
        {
            acquire.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            Assert.True((int)(await acquire.ExecuteScalarAsync())! >= 0);
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingPost = Task.Run(async () =>
        {
            started.SetResult();
            return await journal.PostAsync(pendingSource);
        });
        await started.Task;

        // WHEN reporting while that posting is lock-blocked THEN rows and totals describe the same prior state.
        using (var response = await client.GetAsync("/api/beta/accounting/trial-balance"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("2.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.All(body.RootElement.GetProperty("items").EnumerateArray(), item =>
                Assert.True(item.GetProperty("debitActivity").GetString() is "0.00" or "2.00"));
        }
        // WHEN the lock is released THEN the typed source completes and a fresh report includes it exactly once.
        await transaction.CommitAsync();
        await pendingPost;
        using (var response = await client.GetAsync("/api/beta/accounting/trial-balance"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("7.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.Equal("7.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("credit").GetString());
        }
    }

    [Fact]
    public async Task ReportWaitsForInflightJournalAndReturnsCommittedTotals()
    {
        // GIVEN a typed posting with its journal header written but its line insert held by an SQL table lock.
        await using var journal = await JournalTestContext.OpenAsync(sqlServer);
        await journal.CreateGeneralAccountsAsync();
        await journal.ConfigureAsync();
        await journal.PostAsync(await journal.CreateSourceAsync(amount: "2.00"));
        var source = await journal.CreateSourceAsync(amount: "5.00");
        using var client = journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);
        await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var blocker = (SqlTransaction)await admin.BeginTransactionAsync();
        await using (var holdLines = new SqlCommand("SELECT COUNT(*) FROM Accounting.JournalLines WITH (TABLOCKX,HOLDLOCK)", admin, blocker))
            await holdLines.ExecuteScalarAsync();
        var posting = Task.Run(() => journal.PostAsync(source));
        try
        {
            // AND the dirty header is observed only by this privileged test probe, proving the posting reached the write boundary.
            await WaitForSqlStateAsync(async () =>
            {
                await using var probeConnection = new SqlConnection(journal.Application.AdminConnectionString);
                await probeConnection.OpenAsync();
                await using var probe = new SqlCommand("""
                    SELECT COUNT(*) FROM Accounting.JournalEntries WITH (NOLOCK) WHERE TenantId=@tenant
                    """, probeConnection);
                probe.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
                return (int)(await probe.ExecuteScalarAsync())! == 2;
            });
            var report = client.GetAsync("/api/beta/accounting/trial-balance");

            // WHEN the report reaches its aggregate read THEN SQL shows it waiting on the in-flight posting.
            await WaitForSqlStateAsync(async () =>
            {
                await using var probeConnection = new SqlConnection(journal.Application.AdminConnectionString);
                await probeConnection.OpenAsync();
                await using var probe = new SqlCommand("""
                    SELECT COUNT(*) FROM sys.dm_exec_requests r
                    CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
                    WHERE r.wait_type LIKE N'LCK_M_%'
                      AND t.text LIKE N'%Accounting.JournalLines l%'
                      AND t.text LIKE N'%SUM(CONVERT(decimal(38,4),l.Debit))%'
                    """, probeConnection);
                return (int)(await probe.ExecuteScalarAsync())! > 0;
            });
            Assert.False(report.IsCompleted);

            // WHEN the held line insert is released THEN the atomic posting commits and rows/totals agree.
            await blocker.CommitAsync();
            await posting;
            using var response = await report;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("7.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.Equal("7.00", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("credit").GetString());
            var rows = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal("7.00", rows.Single(row => row.GetProperty("accountId").GetGuid() == journal.DebitAccountId)
                .GetProperty("debitActivity").GetString());
        }
        finally
        {
            if (blocker.Connection is not null) await blocker.RollbackAsync();
            try { await posting; } catch { /* Preserve the original assertion failure. */ }
        }
    }

    private static async Task WaitForSqlStateAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (await condition()) return;
            try { await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) { break; }
        }
        throw new Xunit.Sdk.XunitException("The controlled SQL barrier was not observed before its deadline.");
    }

    [Fact]
    public async Task AccountActivityAndWideTotalsUseFrozenCurrencyScale()
    {
        // GIVEN a two-place policy and a journal from the typed synthetic source.
        await using var journal = await JournalTestContext.OpenAsync(sqlServer);
        await journal.CreateGeneralAccountsAsync();
        await journal.ConfigureAsync(scale: 2);
        var source = await journal.CreateSourceAsync(amount: "3.20");
        var receipt = await journal.PostAsync(source);
        using var client = journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);

        // WHEN reading account lines THEN the posted snapshot and exact two-place amounts appear.
        using (var response = await client.GetAsync($"/api/beta/accounting/accounts/{journal.DebitAccountId}/journal"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var line = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(receipt.JournalId, line.GetProperty("journalId").GetGuid());
            Assert.Equal("Synthetic debit", line.GetProperty("accountName").GetString());
            Assert.Equal("3.20", line.GetProperty("debit").GetString());
            Assert.Equal("3.20", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.Equal("3.20", body.RootElement.GetProperty("pageTotals").GetProperty("debit").GetString());
        }
        // THEN journal and trial totals share the frozen scale without CLR decimal conversion.
        foreach (var route in new[] { "journals", "trial-balance" })
        {
            using var response = await client.GetAsync($"/api/beta/accounting/{route}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("3.20", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.Equal("3.20", body.RootElement.GetProperty("pageTotals").GetProperty("credit").GetString());
        }

        // GIVEN historical account state marked archived in this disposable database,
        // THEN the referenced account remains in the trial balance.
        await using (var connection = new SqlConnection(journal.Application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var archive = new SqlCommand("""
                UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME()
                WHERE TenantId=@tenant AND Id=@account
                """, connection);
            archive.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            archive.Parameters.AddWithValue("@account", journal.DebitAccountId);
            Assert.Equal(1, await archive.ExecuteNonQueryAsync());
        }
        using (var response = await client.GetAsync("/api/beta/accounting/trial-balance"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("items").EnumerateArray()
                .Single(row => row.GetProperty("accountId").GetGuid() == journal.DebitAccountId)
                .GetProperty("isArchived").GetBoolean());
        }
    }

    [Fact]
    public async Task PostedJournalIsTraceableAndCutoffsApplyToAllReports()
    {
        // GIVEN two synthetic source revisions posted with different effective posting dates.
        await using var journal = await JournalTestContext.OpenAsync(sqlServer);
        await journal.CreateGeneralAccountsAsync();
        await journal.ConfigureAsync(scale: 4);
        var first = await journal.CreateSourceAsync(amount: "12.34");
        var firstReceipt = await journal.PostAsync(first, postingDate: new DateTime(2026, 2, 1));
        var second = await journal.CreateSourceAsync(amount: "0.0001");
        var secondReceipt = await journal.PostAsync(second, postingDate: new DateTime(2026, 1, 15));
        using var client = journal.Application.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);

        // WHEN reading detail THEN its frozen source and ordered account snapshots explain the entry.
        using (var response = await client.GetAsync($"/api/beta/accounting/journals/{firstReceipt.JournalId}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(firstReceipt.SourceEventId, body.RootElement.GetProperty("source").GetProperty("id").GetGuid());
            Assert.Equal(first.Id, body.RootElement.GetProperty("source").GetProperty("sourceId").GetGuid());
            Assert.Equal("12.3400", body.RootElement.GetProperty("header").GetProperty("debitTotal").GetString());
            var lines = body.RootElement.GetProperty("lines").EnumerateArray().ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Equal("Synthetic debit", lines[0].GetProperty("accountName").GetString());
            Assert.Equal("Synthetic credit", lines[1].GetProperty("accountName").GetString());
        }
        // GIVEN a reporting reader in another tenant THEN the first tenant's journal remains unavailable.
        await using (var connection = new SqlConnection(journal.Application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var grant = new SqlCommand("""
                INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
                    SELECT @tenant,@user,RoleId FROM Administration.AccountingRoles
                    WHERE TenantId=@tenant AND Kind='Reader'
                """, connection);
            grant.Parameters.AddWithValue("@tenant", JournalTestContext.OtherTenantId);
            grant.Parameters.AddWithValue("@user", AuthTestApplication.OtherTenantUserId);
            await grant.ExecuteNonQueryAsync();
        }
        using var otherTenant = journal.Application.CreateClient();
        await LoginAsync(otherTenant, "other@example.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherTenant.GetAsync($"/api/beta/accounting/journals/{firstReceipt.JournalId}")).StatusCode);

        // WHEN using inclusive posting and recorded cutoffs THEN every report includes only eligible activity.
        var early = "/api/beta/accounting/journals?postingThrough=2026-01-31";
        using (var response = await client.GetAsync(early))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(items);
            Assert.Equal(secondReceipt.JournalId, items[0].GetProperty("id").GetGuid());
        }
        using (var response = await client.GetAsync($"/api/beta/accounting/trial-balance?recordedThrough={Uri.EscapeDataString(firstReceipt.RecordedAtUtc.AddTicks(-1).ToString("O"))}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("0.0000", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
        }
        using (var response = await client.GetAsync("/api/beta/accounting/trial-balance"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var rows = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal("12.3401", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("debit").GetString());
            Assert.Equal("12.3401", body.RootElement.GetProperty("wholeFilterTotals").GetProperty("credit").GetString());
            Assert.Contains(rows, row => row.GetProperty("debitMinusCredit").GetString() == "12.3401");
            Assert.Contains(rows, row => row.GetProperty("debitMinusCredit").GetString() == "-12.3401");
        }

        // WHEN traversing a page THEN the protected cursor preserves both cutoffs and route scope.
        using (var firstPage = await client.GetAsync("/api/beta/accounting/journals?pageSize=1"))
        {
            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
            using var body = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            var cursor = body.RootElement.GetProperty("nextCursor").GetString();
            Assert.False(string.IsNullOrEmpty(cursor));
            using var secondPage = await client.GetAsync($"/api/beta/accounting/journals?pageSize=1&cursor={Uri.EscapeDataString(cursor!)}");
            Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
            using var nextBody = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
            Assert.Single(nextBody.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(body.RootElement.GetProperty("recordedThrough").GetDateTimeOffset(),
                nextBody.RootElement.GetProperty("recordedThrough").GetDateTimeOffset());
            var echoedCutoff = body.RootElement.GetProperty("recordedThrough").GetString();
            using var repeated = await client.GetAsync($"/api/beta/accounting/journals?recordedThrough={Uri.EscapeDataString(echoedCutoff!)}");
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"/api/beta/accounting/trial-balance?pageSize=1&cursor={Uri.EscapeDataString(cursor!)}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"/api/beta/accounting/journals?pageSize=1&postingThrough=2026-01-31&cursor={Uri.EscapeDataString(cursor!)}")).StatusCode);
        }
    }

    [Fact]
    public async Task ReportPermissionIsIndependentAndResponsesArePrivate()
    {
        // GIVEN an authenticated tenant administrator without accounting roles.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);

        // WHEN requesting a journal report THEN accounting-report authority is required.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/beta/accounting/journals")).StatusCode);

        // GIVEN a reader-only grant to another tenant user.
        await using (var connection = new SqlConnection(app.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var grant = new SqlCommand("""
                INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
                    SELECT @tenant,@user,RoleId FROM Administration.AccountingRoles
                    WHERE TenantId=@tenant AND Kind='Reader'
                """, connection);
            grant.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
            grant.Parameters.AddWithValue("@user", AuthTestApplication.MemberUserId);
            await grant.ExecuteNonQueryAsync();
        }
        using var reader = app.CreateClient();
        await LoginAsync(reader, "member@example.com");
        // WHEN the reader visits reports THEN setup authority is not required.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/beta/accounting/journals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/beta/accounting/setup")).StatusCode);

        // GIVEN the accounting administrator role, which grants reporting authority.
        await AccountingEndpointTests.GrantSetup(client);
        // WHEN requesting each empty report THEN each response is private and complete.
        foreach (var path in new[] { "/api/beta/accounting/journals", "/api/beta/accounting/trial-balance" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("9999-12-31", body.RootElement.GetProperty("postingThrough").GetString());
            Assert.NotEqual(default, body.RootElement.GetProperty("recordedThrough").GetDateTimeOffset());
        }
    }

    [Fact]
    public async Task MissingIdsAndInvalidReportFiltersDoNotExposeData()
    {
        // GIVEN an authorized report reader and an empty journal.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client, AuthTestApplication.AdminEmail);
        await AccountingEndpointTests.GrantSetup(client);

        // WHEN requesting unavailable tenant-scoped details THEN the API returns 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/beta/accounting/journals/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/beta/accounting/accounts/{Guid.NewGuid()}/journal")).StatusCode);

        // WHEN supplying invalid cutoffs, page limits or cursors THEN each report rejects them.
        foreach (var route in new[] { "journals", "trial-balance", $"accounts/{Guid.NewGuid()}/journal" })
        {
            foreach (var filter in new[] { "postingThrough=not-a-date", "recordedThrough=not-an-instant", "pageSize=101", "cursor=broken" })
            {
                using var response = await client.GetAsync($"/api/beta/accounting/{route}?{filter}");
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
        }
    }
}
