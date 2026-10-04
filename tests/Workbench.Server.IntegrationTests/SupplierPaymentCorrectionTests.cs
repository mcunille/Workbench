// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierPaymentCorrectionTests(SqlServerFixture sqlServer, SupplierCorrectionScenarios scenarios) : IClassFixture<SupplierCorrectionScenarios>
{
    [Fact]
    public async Task ReplacementPreviewUsesRecordCommandValidation()
    {
        // GIVEN a genuine paid bill and a complete, valid explicit replacement.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("100");
        var original = await context.CommandAsync(); await context.AllocateAsync(original, bill, "100"); await context.RecordAsync(original);
        var replacement = await context.CommandAsync("100", "2026-09-20"); await context.AllocateAsync(replacement, bill, "100");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, Guid.Parse(original["paymentId"]!.ToString()), replacement);
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd");
        var futurePosting = correction.DeepClone().AsObject();
        futurePosting["replacement"]!["postingDate"] = future; futurePosting["replacement"]!["effectiveDate"] = future;
        // AND a future open posting date remains valid for payment already made.
        await SupplierCorrectionFixture.PreviewAsync(context, futurePosting);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        var errors = new List<Exception?>();
        // WHEN replacement shape, identity, method, text bounds or actual cash date cannot be recorded.
        foreach (var invalid in new[] { "method", "identity", "text", "type", "futurePayment" })
        {
            var bad = correction.DeepClone().AsObject();
            switch (invalid)
            {
                case "method": bad["replacement"]!["method"] = "Wire"; break;
                case "identity": bad["replacement"]!["paymentId"] = Guid.Empty.ToString(); break;
                case "text": bad["replacement"]!["notes"] = new string('x', 2001); break;
                case "type": bad["replacement"]!["schemaVersion"] = "1"; break;
                case "futurePayment":
                    bad["replacement"]!["paymentDate"] = future; bad["replacement"]!["postingDate"] = future; bad["replacement"]!["effectiveDate"] = future;
                    break;
            }
            errors.Add(await Record.ExceptionAsync(() => SupplierCorrectionFixture.PreviewAsync(context, bad)));
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        }
        // AND a new payment identity cannot alias a genuine existing bill item.
        var collision = correction.DeepClone().AsObject(); collision["replacement"]!["paymentId"] = bill.ToString();
        var collisionPreview = await Record.ExceptionAsync(async () =>
        {
            var accepted = await SupplierCorrectionFixture.PreviewAsync(context, collision);
            collision["expectedPlanFingerprint"] = accepted["fingerprint"]!.DeepClone();
        });
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        var collisionExecution = await Record.ExceptionAsync(() => context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), collision));
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        // THEN the valid composed owner succeeds and every unrecordable preview is rejected without writes.
        await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), correction);
        Assert.Equal(0m, await context.Allocation.BalanceAsync(bill));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPaymentCorrections"));
        Assert.All(errors, error => Assert.Equal(51000, Assert.IsType<SqlException>(error).Number));
        Assert.All(new[] { collisionPreview, collisionExecution }, error =>
        {
            Assert.Equal(51009, Assert.IsType<SqlException>(error).Number);
            Assert.Contains("Replacement source", error!.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CorrectionEnvelopesAreBoundedBeforeDependencyParsing()
    {
        // GIVEN an authorized actor and a valid real payment correction.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var input = await SupplierCorrectionFixture.CorrectionAsync(context, Guid.Parse(payment["paymentId"]!.ToString()));
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        var errors = new List<SqlException>();
        // WHEN malformed or oversized input reaches each public correction boundary.
        foreach (var preview in new[] { true, false })
            foreach (var invalid in new[] { "{", input.ToJsonString() + new string(' ', 131073) })
            {
                await using var sql = new SqlCommand(preview
                    ? "EXEC Purchasing.PreviewSupplierPaymentCorrection @actor,@session,@input"
                    : "EXEC Purchasing.CorrectSupplierPayment @actor,@session,@request,@input", context.Allocation.Journal.Connection);
                sql.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); sql.Parameters.AddWithValue("@session", context.Allocation.Journal.SessionId);
                sql.Parameters.AddWithValue("@request", Guid.NewGuid()); sql.Parameters.AddWithValue("@input", invalid);
                errors.Add(await Assert.ThrowsAsync<SqlException>(() => sql.ExecuteScalarAsync()));
                Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
            }
        // THEN valid authority still permits the actual owner, while both failures use the bounded-envelope contract.
        await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), input);
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPaymentCorrections"));
        Assert.All(errors, error => Assert.Equal(51000, error.Number));
    }

    [Fact]
    public async Task UnsupportedCorrectedApplicationRejectsEveryDependentOwner()
    {
        // GIVEN a genuine standalone application and a privileged future adapter using the real inverse kernel.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("100"); var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var funding = Guid.Parse(payment["paymentId"]!.ToString());
        var applied = await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill));
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, funding);
        var reverse = await SupplierCorrectionFixture.ReverseAsync(context, Guid.Parse(applied["applicationIds"]![0]!.ToString()));
        await context.Bills.AdminAsync("""
            CREATE PROCEDURE Purchasing.UnsupportedDependencyFixture
              @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
            WITH EXECUTE AS OWNER AS BEGIN
              SET XACT_ABORT ON; SET NOCOUNT ON;
              BEGIN TRY
                BEGIN TRAN;
                DECLARE @Journal uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.journalId')),
                  @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.configuration')),@Evidence nvarchar(max);
                SET @Evidence=(SELECT 1 schemaVersion,CONVERT(nvarchar(36),s.SourceRevision) originalSourceRevision,
                  CONVERT(varchar(64),s.SnapshotSha256,2) originalSnapshotSha256 FROM Accounting.SourceEvents s
                  JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id WHERE j.Id=@Journal FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
                DECLARE @Result TABLE(CorrectionId uniqueidentifier,ReversalJournalId uniqueidentifier,ReplacementJournalId uniqueidentifier,ReplacementSourceRevision uniqueidentifier,RecordedAtUtc datetimeoffset);
                INSERT @Result EXEC Accounting.CorrectJournalCore @ActorId,@SessionId,@RequestId,N'SupplierPaymentsCorrect',N'Fixture.UnsupportedDependency',1,
                  @Command,@Journal,@Config,'2026-09-20',N'Future source owner',@Evidence;
                COMMIT; SELECT N'{}' ResultJson;
              END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
            END;
            """);
        await context.Bills.AdminAsync("GRANT EXECUTE ON Purchasing.UnsupportedDependencyFixture TO workbench_web");
        await context.Bills.ExecuteAsync("UnsupportedDependencyFixture", Guid.NewGuid(), new JsonObject
        { ["journalId"] = applied["journalIds"]![0]!.DeepClone(), ["configuration"] = context.Allocation.Journal.ConfigurationVersion.ToString() });
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.CorrectionGroups"));
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        var errors = new List<Exception?>();
        // WHEN each production owner sees the retained unsupported correction, isolate even an incorrectly accepted attempt.
        foreach (var operation in new[] { "Preview", "CorrectSupplierPayment", "ReverseSupplierApplication" })
        {
            await using var begin = new SqlCommand("BEGIN TRAN", context.Allocation.Journal.Connection); await begin.ExecuteNonQueryAsync();
            try
            {
                errors.Add(await Record.ExceptionAsync(async () =>
                {
                    if (operation == "Preview") await SupplierCorrectionFixture.PreviewAsync(context, correction);
                    else await context.Bills.ExecuteAsync(operation, Guid.NewGuid(), operation == "ReverseSupplierApplication" ? reverse : correction);
                }));
            }
            finally
            {
                await using var rollback = new SqlCommand("IF @@TRANCOUNT>0 ROLLBACK", context.Allocation.Journal.Connection); await rollback.ExecuteNonQueryAsync();
            }
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        }
        // THEN no owner doubles the inverse or presents a usable partial dependency plan.
        Assert.All(errors, error => Assert.Equal(51009, Assert.IsType<SqlException>(error).Number));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplacementEvidenceWaitsForCompleteCoordinationBeforeLocking(bool applicationCoordination)
    {
        // GIVEN a genuine application and replacement document, with no financial changes during preview.
        await using var prepared = await scenarios.OpenAsync("coordination");
        var context = prepared.Context;
        var funding = Guid.Parse(prepared.Data["funding"]!.ToString());
        var application = Guid.Parse(prepared.Data["application"]!.ToString());
        var document = Guid.Parse(prepared.Data["document"]!.ToString());
        var replacement = prepared.Data["replacement"]!.AsObject();
        var correction = prepared.Data["correction"]!.AsObject();
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        var evidenceErrors = new List<Exception?>();
        await using var holder = new SqlConnection(context.Allocation.Journal.Application.AdminConnectionString); await holder.OpenAsync();
        await using var observer = new SqlConnection(context.Allocation.Journal.Application.AdminConnectionString); await observer.OpenAsync();
        {
            var coordination = applicationCoordination ? "SupplierApplicationVersions" : "SupplierItemVersions";
            var identity = applicationCoordination ? "ApplicationId" : "ItemId";
            await using (var begin = new SqlCommand("BEGIN TRAN", holder)) await begin.ExecuteNonQueryAsync();
            await using (var hold = new SqlCommand($"SELECT COUNT(*) FROM Purchasing.{coordination} WITH(UPDLOCK,HOLDLOCK,ROWLOCK) WHERE TenantId=@tenant AND {identity}=@id", holder))
            {
                hold.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId); hold.Parameters.AddWithValue("@id", applicationCoordination ? application : funding);
                Assert.Equal(1, (int)(await hold.ExecuteScalarAsync())!);
            }
            // WHEN preview waits for either application or final affected-item coordination.
            var pending = SupplierCorrectionFixture.PreviewAsync(context, correction);
            try
            {
                await using var wait = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id=@waiter AND blocking_session_id=@holder AND wait_type LIKE 'LCK_M_%' AND (wait_resource LIKE 'KEY:%' OR wait_resource LIKE 'PAGE:%' OR wait_resource LIKE 'OBJECT:%')", observer);
                wait.Parameters.AddWithValue("@waiter", context.Allocation.Journal.Connection.ServerProcessId); wait.Parameters.AddWithValue("@holder", holder.ServerProcessId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                while ((int)(await wait.ExecuteScalarAsync(timeout.Token))! != 1) await Task.Yield();
                // THEN an independent genuine evidence validation can still acquire its document/storage locks immediately.
                await using (var begin = new SqlCommand("SET LOCK_TIMEOUT 0; BEGIN TRAN", observer)) await begin.ExecuteNonQueryAsync();
                await using var evidence = new SqlCommand("DECLARE @validated nvarchar(max); EXEC Purchasing.ValidateBillEvidence @tenant,@po,@input,@validated OUTPUT; SELECT @validated;", observer);
                evidence.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId); evidence.Parameters.AddWithValue("@po", context.Bills.Recognition.PurchaseOrderId);
                evidence.Parameters.AddWithValue("@input", replacement["evidence"]!.ToJsonString());
                evidenceErrors.Add(await Record.ExceptionAsync(async () => Assert.Contains(document.ToString(), (string)(await evidence.ExecuteScalarAsync())!, StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                await using var releaseEvidence = new SqlCommand("IF @@TRANCOUNT>0 ROLLBACK; SET LOCK_TIMEOUT -1", observer); await releaseEvidence.ExecuteNonQueryAsync();
                await using var releaseCoordination = new SqlCommand("IF @@TRANCOUNT>0 ROLLBACK", holder); await releaseCoordination.ExecuteNonQueryAsync();
                var plan = await pending;
                Assert.Equal(correction["expectedPlanFingerprint"]!.ToString(), plan["fingerprint"]!.ToString());
            }
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        }
        Assert.All(evidenceErrors, error => Assert.Null(error));
    }

    [Fact]
    public async Task ReplacementPreviewRequiresAvailableEvidenceWithoutWrites()
    {
        // GIVEN a replacement with a real available document owned by this PO.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var (document, revision) = await context.Bills.SeedDocumentAsync();
        var replacement = await context.CommandAsync("80", "2026-09-20");
        replacement["evidence"] = new JsonObject { ["documents"] = new JsonArray(new JsonObject { ["documentId"] = document.ToString(), ["revisionId"] = revision.ToString() }), ["missingEvidenceReason"] = null };
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, Guid.Parse(payment["paymentId"]!.ToString()), replacement);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        // WHEN evidence is omitted without a reason or its actual document is later removed.
        var missing = correction.DeepClone().AsObject(); missing["replacement"]!["evidence"]!["documents"] = new JsonArray();
        var missingError = await Record.ExceptionAsync(() => SupplierCorrectionFixture.PreviewAsync(context, missing));
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        await context.Bills.AdminAsync($"UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME() WHERE Id='{document}'");
        before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        var previewError = await Record.ExceptionAsync(() => SupplierCorrectionFixture.PreviewAsync(context, correction));
        var executionError = await Record.ExceptionAsync(() => context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), correction));
        // THEN preview and execution agree on unavailable evidence, preserving every financial row.
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        Assert.Equal(51000, Assert.IsType<SqlException>(missingError).Number);
        Assert.Equal(51004, Assert.IsType<SqlException>(previewError).Number);
        Assert.Equal(51004, Assert.IsType<SqlException>(executionError).Number);
    }

    [Fact]
    public async Task ReplacementPreviewChecksCurrentAndLaterPostingCapacity()
    {
        // GIVEN an original allocation and another genuine payment that settles the remainder at a later date.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("150");
        var original = await context.CommandAsync(); await context.AllocateAsync(original, bill, "100"); await context.RecordAsync(original);
        var later = await context.CommandAsync("50", "2026-09-25"); await context.AllocateAsync(later, bill, "50"); await context.RecordAsync(later);
        var replacement = await context.CommandAsync("100", "2026-09-20"); await context.AllocateAsync(replacement, bill, "100");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, Guid.Parse(original["paymentId"]!.ToString()), replacement);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        var errors = new List<Exception?>();
        // WHEN replacement funding covers its allocations but the debt is exhausted now or at the retained later boundary.
        foreach (var amount in new[] { "160", "150" })
        {
            var bad = correction.DeepClone().AsObject(); bad["replacement"]!["amount"] = amount; bad["replacement"]!["allocations"]![0]!["amount"] = amount;
            errors.Add(await Record.ExceptionAsync(() => SupplierCorrectionFixture.PreviewAsync(context, bad)));
            errors.Add(await Record.ExceptionAsync(() => context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), bad)));
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        }
        // THEN the valid plan still commits and preserves later cash, while both invalid plans fail without writes.
        await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), correction);
        Assert.Equal(0m, await context.Allocation.BalanceAsync(bill));
        Assert.Equal(-150m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
        Assert.All(errors, error => Assert.Equal(51009, Assert.IsType<SqlException>(error).Number));
        Assert.All(errors, error => Assert.Contains("capacity is unavailable at a posting boundary", error!.Message));
    }

    [Fact]
    public async Task HistoricalPaymentProofRequiresExactDebtMovementCoverage()
    {
        // GIVEN equal allocations on two genuinely different historical account versions.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("50");
        await context.Bills.AdminAsync("UPDATE Accounting.Accounts SET Version=NEWID(),Name='Later payable snapshot' WHERE Purpose='SupplierPayable'");
        var otherBill = await context.Allocation.BillAsync("50");
        var payment = await context.CommandAsync(); await context.AllocateAsync(payment, bill, "50"); await context.AllocateAsync(payment, otherBill, "50");
        await context.RecordAsync(payment); var funding = Guid.Parse(payment["paymentId"]!.ToString());
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, funding);
        var group = await context.Bills.ScalarAsync<Guid>($"SELECT GroupId FROM Purchasing.SupplierItemMovements WHERE ItemId='{bill}' AND EventKind='Apply'");
        var itemControl = $"SELECT COUNT(*) FROM Purchasing.SupplierItemControl('{JournalTestContext.TenantId}','{funding}')";
        var paymentControl = $"SELECT COUNT(*) FROM Purchasing.SupplierPaymentControl('{JournalTestContext.TenantId}','{funding}')";
        Assert.Equal(1, await context.Bills.ScalarAsync<int>(itemControl));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>(paymentControl));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(DISTINCT Ordinal) FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}'"));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(DISTINCT AccountVersion) FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}'"));
        var firstMovement = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}' AND ItemId='{bill}' AND EventKind='Apply'");
        var secondMovement = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}' AND ItemId='{otherBill}' AND EventKind='Apply'");
        var firstAttribution = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}' AND MovementId='{firstMovement}'");
        var secondAttribution = await context.Bills.ScalarAsync<Guid>($"SELECT Id FROM Purchasing.SupplierControlAttributions WHERE GroupId='{group}' AND MovementId='{secondMovement}'");
        foreach (var swapBoth in new[] { true, false })
        {
            try
            {
                // WHEN equal debt attributions are swapped, or both point to one movement, totals still agree.
                if (swapBoth) await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierControlAttributions SET MovementId='{secondMovement}' WHERE Id='{firstAttribution}'");
                await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierControlAttributions SET MovementId='{firstMovement}' WHERE Id='{secondAttribution}'");
                var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
                // THEN neither historical item authority nor correction authority can accept incomplete ownership.
                Assert.Equal(0, await context.Bills.ScalarAsync<int>(itemControl));
                Assert.Equal(0, await context.Bills.ScalarAsync<int>(paymentControl));
                Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => SupplierCorrectionFixture.PreviewAsync(context, correction))).Number);
                Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
            }
            finally
            {
                await context.Bills.AdminAsync($"UPDATE Purchasing.SupplierControlAttributions SET MovementId='{firstMovement}' WHERE Id='{firstAttribution}'; UPDATE Purchasing.SupplierControlAttributions SET MovementId='{secondMovement}' WHERE Id='{secondAttribution}';");
            }
            // AND exact restoration proves each hostile probe started from valid evidence.
            Assert.Equal(1, await context.Bills.ScalarAsync<int>(itemControl));
            Assert.Equal(1, await context.Bills.ScalarAsync<int>(paymentControl));
        }
    }

    [Fact]
    public async Task UnsupportedRetainedDependencyRejectsPreviewAndExecutionWithoutPartialInverse()
    {
        // GIVEN a real payment/application plus an external release without a supported owner-resolution adapter.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("100"); var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var funding = Guid.Parse(payment["paymentId"]!.ToString());
        await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(funding, bill));
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, funding);
        await context.Allocation.SeedLaterReleaseAsync(); correction["postingDate"] = "2026-09-25";
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        // WHEN the entire retained graph is inspected at a date after that release THEN no source is partially undone.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => SupplierCorrectionFixture.PreviewAsync(context, correction))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), correction))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
    }

    [Fact]
    public async Task DependentCorrectionRequiresAllocationAuthorityAndPreservesHistoricalAccountSnapshots()
    {
        // GIVEN genuine embedded history whose original control snapshots have since been archived and renamed.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("100"); var payment = await context.CommandAsync(); await context.AllocateAsync(payment, bill, "100");
        await context.RecordAsync(payment); var id = Guid.Parse(payment["paymentId"]!.ToString());
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, id);
        await context.Bills.AdminAsync("UPDATE Accounting.Accounts SET ArchivedAtUtc=SYSUTCDATETIME(),Version=NEWID(),Name='Changed after posting' WHERE Purpose IN('SupplierPayable','SupplierAdvance','Bank'); DELETE FROM [Identity].RoleClaims WHERE ClaimValue='SupplierAllocationsManage';");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        // WHEN Manage is missing THEN preview and execution deny before disclosing or writing dependency effects.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => SupplierCorrectionFixture.PreviewAsync(context, correction))).Number);
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), correction))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        await context.Bills.AdminAsync("INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue) SELECT TenantId,RoleId,N'workbench/permission',N'SupplierAllocationsManage' FROM Administration.AccountingRoles WHERE Kind='Administrator';");
        // WHEN the owner has both required permissions THEN archival cannot redirect an exact historical inverse.
        var result = await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), correction);
        Assert.Equal(100m, await context.Allocation.BalanceAsync(bill)); Assert.Equal(0m, await context.Allocation.BalanceAsync(id));
        // AND the embedded allocation restores debt once and removes cash once.
        Assert.Equal(0m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPaymentCorrections"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.CorrectionGroups c JOIN Accounting.JournalLines old ON old.JournalId=c.OriginalJournalId JOIN Accounting.JournalLines inv ON inv.JournalId=c.ReversalJournalId AND inv.Ordinal=old.Ordinal WHERE old.AccountId<>inv.AccountId OR old.AccountVersion<>inv.AccountVersion OR old.AccountName<>inv.AccountName OR old.AccountCode<>inv.AccountCode OR old.AccountType<>inv.AccountType OR old.AccountPurpose<>inv.AccountPurpose OR old.Debit<>inv.Credit OR old.Credit<>inv.Debit"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m JOIN Accounting.SourceEvents s ON s.Id=m.SourceEventId JOIN Accounting.JournalEntries j ON j.SourceEventId=s.Id JOIN Purchasing.SupplierFinancialGroups g ON g.Id=m.GroupId WHERE m.GroupId='{result["groupId"]}' AND (m.RecordedAtUtc<>g.RecordedAtUtc OR s.RecordedAtUtc<>g.RecordedAtUtc OR j.RecordedAtUtc<>g.RecordedAtUtc)"));
    }

    [Fact]
    public async Task ActuallyCorrectedPaymentStillPreventsSupplierReassignment()
    {
        // GIVEN an otherwise history-free PO whose only real payment has actually been corrected.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        await context.Bills.AdminAsync("UPDATE Purchasing.DraftOrders SET PoNumber=1");
        context.Bills.Recognition.PurchaseOrderVersion = await context.Bills.ScalarAsync<string>("SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.DraftOrders");
        var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), await SupplierCorrectionFixture.CorrectionAsync(context, Guid.Parse(payment["paymentId"]!.ToString())));
        var draft = Workbench.Server.Purchasing.DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierId = null, SupplierName = "Replacement supplier", Notes = "Amendment", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] });
        await using var command = new SqlCommand("Purchasing.SavePurchaseOrder", context.Allocation.Journal.Connection) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", Guid.NewGuid()); command.Parameters.AddWithValue("@ActorUserId", JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@TargetId", context.Bills.Recognition.PurchaseOrderId); command.Parameters.AddWithValue("@ExpectedVersion", Convert.FromHexString(context.Bills.Recognition.PurchaseOrderVersion[2..]));
        command.Parameters.AddWithValue("@Operation", "Amend"); command.Parameters.AddWithValue("@OrderDate", "2026-01-01"); command.Parameters.AddWithValue("@Reason", "Amendment");
        command.Parameters.AddWithValue("@Draft", System.Text.Json.JsonSerializer.Serialize(draft, Workbench.Server.Purchasing.DraftOrderInput.JsonOptions)); command.Parameters.AddWithValue("@Calculation", "{}");
        // WHEN the PO supplier is removed THEN immutable corrected history still owns the original supplier.
        Assert.Equal(50415, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPaymentCorrections"));
    }

    [Fact]
    public async Task PreviewIncludesReplacementEffectsAndRejectsClosedReplacementWithoutWrites()
    {
        // GIVEN a paid bill and a smaller fully allocated replacement.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync("150");
        var payment = await context.CommandAsync(); await context.AllocateAsync(payment, bill, "100"); await context.RecordAsync(payment);
        var id = Guid.Parse(payment["paymentId"]!.ToString());
        var replacement = await context.CommandAsync("80", "2026-09-20"); await context.AllocateAsync(replacement, bill, "80");
        var command = await SupplierCorrectionFixture.CorrectionAsync(context, id, replacement);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        // WHEN preview is repeated THEN it deterministically describes the complete inverse plus replacement.
        var plan = await SupplierCorrectionFixture.PreviewAsync(context, command);
        Assert.Equal(plan.ToJsonString(), (await SupplierCorrectionFixture.PreviewAsync(context, command)).ToJsonString());
        Assert.Equal(20m, decimal.Parse(plan["effects"]!.AsArray().Single(x => Guid.Parse(x!["itemId"]!.ToString()) == bill)!["amount"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(plan["effects"]!.AsArray(), x => Guid.Parse(x!["itemId"]!.ToString()) == Guid.Parse(replacement["paymentId"]!.ToString()));
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        // AND an open correction date cannot conceal a replacement in a closed source month.
        await context.Bills.AdminAsync(JournalControlAdapterSql.Install);
        await using var close = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,@ExpectedConfigurationVersion=@version,@PeriodStart='2026-09-01',@Reason=N'Reconciled',@Evidence=NULL", context.Allocation.Journal.Connection);
        close.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); close.Parameters.AddWithValue("@session", context.Allocation.Journal.SessionId);
        close.Parameters.AddWithValue("@request", Guid.NewGuid()); close.Parameters.AddWithValue("@version", context.Allocation.Journal.ConfigurationVersion);
        await close.ExecuteNonQueryAsync();
        command["postingDate"] = "2026-10-01";
        before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => SupplierCorrectionFixture.PreviewAsync(context, command))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
    }

    [Fact]
    public async Task RetainedClosureCountsReversalsAndRejects1001BeforeEvidenceValidation()
    {
        // GIVEN genuine payment, application and reversal evidence for a small complete closure.
        await using var prepared = await scenarios.OpenAsync("closure");
        var context = prepared.Context;
        var application = Guid.Parse(prepared.Data["application"]!.ToString());
        var command = prepared.Data["command"]!.AsObject();
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        // WHEN previewing real history THEN the payment, application and reversal all appear without writes.
        var plan = await SupplierCorrectionFixture.PreviewAsync(context, command);
        Assert.Equal(3, plan["eventCount"]!.GetValue<int>());
        Assert.Single(plan["applications"]!.AsArray());
        Assert.Equal(application, Guid.Parse(plan["applications"]![0]!["id"]!.GetValue<string>()));
        Assert.NotNull(plan["applications"]![0]!["reversalId"]);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        // GIVEN 997 additional identities with valid foreign keys, but without posting evidence.
        // This isolates the closure guard from the cost of replaying hundreds of financial commands.
        await SeedApplications(997);
        before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        // WHEN the closure contains exactly 1000 events THEN it passes the cap and reaches the evidence guard.
        var error = await Assert.ThrowsAsync<SqlException>(() => SupplierCorrectionFixture.PreviewAsync(context, command));
        Assert.Equal(51009, error.Number);
        Assert.Contains("Supplier correction has an unsupported or later dependency.", error.Message);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        // AND a 1001st event fails the cap itself, before any evidence validation or truncated plan.
        await SeedApplications(1);
        before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        error = await Assert.ThrowsAsync<SqlException>(() => SupplierCorrectionFixture.PreviewAsync(context, command));
        Assert.Equal(51000, error.Number);
        Assert.Contains("Supplier correction closure exceeds 1000 events.", error.Message);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));

        Task SeedApplications(int count) => context.Bills.AdminAsync($"""
            INSERT Purchasing.SupplierApplications(TenantId,Id,GroupId,FundingItemId,DebtItemId,PostingDate,Amount,ActorId,RecordedAtUtc)
              SELECT a.TenantId,NEWID(),a.GroupId,a.FundingItemId,a.DebtItemId,a.PostingDate,a.Amount,a.ActorId,a.RecordedAtUtc
              FROM Purchasing.SupplierApplications a
              CROSS JOIN (SELECT TOP ({count}) object_id FROM sys.all_objects) identities
              WHERE a.Id='{application}';
            """);
    }

    [Theory]
    [InlineData("ApplySupplierFunds", true)]
    [InlineData("ApplySupplierFunds", false)]
    [InlineData("ReverseSupplierApplication", true)]
    [InlineData("ReverseSupplierApplication", false)]
    [InlineData("CorrectSupplierPayment", true)]
    [InlineData("RecordSupplierPayment", true)]
    [InlineData("RecordSupplierPayment", false)]
    public async Task CorrectionAndDependencyCommandsRecheckBothSerialOrders(string contender, bool correctionFirst)
    {
        // GIVEN a correction preview and a competing command using the same current dependency versions.
        await using var prepared = await scenarios.OpenAsync("bill200");
        var context = prepared.Context;
        var bill = Guid.Parse(prepared.Data["bill"]!.GetValue<string>());
        var payment = await context.CommandAsync();
        if (contender == "ReverseSupplierApplication") await context.AllocateAsync(payment, bill, "100");
        var posted = await context.RecordAsync(payment);
        var id = Guid.Parse(payment["paymentId"]!.ToString());
        var replacement = contender == "RecordSupplierPayment" ? await context.CommandAsync("100", "2026-09-20") : null;
        if (replacement is not null) await context.AllocateAsync(replacement, bill, "100");
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, id, replacement);
        JsonObject other;
        if (contender == "ReverseSupplierApplication") other = await SupplierCorrectionFixture.ReverseAsync(context, Guid.Parse(posted["applicationIds"]![0]!.ToString()));
        else if (contender == "CorrectSupplierPayment") other = correction.DeepClone().AsObject();
        else if (contender == "ApplySupplierFunds") other = await context.Allocation.CommandAsync(id, bill, "100", "2026-09-20");
        else { other = await context.CommandAsync("100", "2026-09-20"); await context.AllocateAsync(other, bill, "100"); }
        await using var sibling = await context.Allocation.Journal.OpenSiblingAsync();
        var successes = 0;
        async Task Correct() { await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), correction); successes++; }
        async Task Compete() { await context.Bills.ExecuteAsync(contender, Guid.NewGuid(), other, sibling); successes++; }
        // WHEN independent restricted connections demonstrate blocking in each accounting-lock order.
        var error = await PurchaseRecognitionConcurrencyTests.InOrderAsync(context.Bills.Recognition,
            correctionFirst ? context.Allocation.Journal.Connection : sibling, correctionFirst ? sibling : context.Allocation.Journal.Connection,
            correctionFirst ? Correct : Compete, correctionFirst ? Compete : Correct);
        // THEN exactly one changes the graph and the stale contender leaves no detached inverse or overspend.
        Assert.Equal(51009, error?.Number);
        Assert.Equal(1, successes);
        Assert.Equal(correctionFirst || contender == "CorrectSupplierPayment" ? 1 : 0,
            await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPaymentCorrections"));
        Assert.True(await context.Bills.ScalarAsync<decimal>("SELECT MIN(Balance) FROM(SELECT SUM(SUM(Amount)) OVER(PARTITION BY ItemId ORDER BY PostingDate ROWS UNBOUNDED PRECEDING) Balance FROM Purchasing.SupplierItemMovements GROUP BY ItemId,PostingDate) b") >= 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalReceiptFailureRollsBackWholeOwnerAndReplayRequiresCurrentAuthority(bool reverse)
    {
        // GIVEN a real allocated payment and a composed owner with explicit replacement or reapplication.
        await using var prepared = await scenarios.OpenAsync(reverse ? "receiptTrue" : "receiptFalse");
        var context = prepared.Context;
        var input = prepared.Data["input"]!.AsObject();
        var operation = reverse ? "ReverseSupplierApplication" : "CorrectSupplierPayment";
        var request = Guid.NewGuid(); var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        await context.Bills.AdminAsync("CREATE TRIGGER Purchasing.FailFinalSupplierReceipt ON Purchasing.SupplierFinancialReceipts AFTER UPDATE AS IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.TenantId=i.TenantId AND d.RequestId=i.RequestId WHERE JSON_VALUE(d.ResultJson,'$.state')='pending' AND JSON_VALUE(i.ResultJson,'$.state') IS NULL) THROW 51995,'Disposable late owner fault.',1;");
        try
        {
            // WHEN the last write fails after real inverse and replacement effects exist in the transaction.
            Assert.Equal(51995, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync(operation, request, input))).Number);
            // THEN all durable financial bytes, versions and pending receipts roll back.
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        }
        finally { await context.Bills.AdminAsync("DROP TRIGGER Purchasing.FailFinalSupplierReceipt"); }
        var original = await SupplierOpenItemSecurityTests.ExecuteRawAsync(context, operation, request, input);
        Assert.Equal(original, await SupplierOpenItemSecurityTests.ExecuteRawAsync(context, operation, request, input));
        var finalized = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync(operation, Guid.NewGuid(), input))).Number);
        // AND a finalized owner cannot authorize even an exact child replay through a protected composition core.
        await context.Bills.AdminAsync("""
            CREATE PROCEDURE Purchasing.TryFinalizedSupplierComposition @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
            WITH EXECUTE AS OWNER AS BEGIN
              SET NOCOUNT ON; SET XACT_ABORT ON;
              BEGIN TRY BEGIN TRAN;
                DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Resource nvarchar(255),
                  @Child uniqueidentifier,@Owner uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.owner')),@Input nvarchar(max),@Operation nvarchar(40);
                SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
                EXEC sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction';
                SELECT @Child=c.RequestId,@Input=c.CanonicalInput,@Operation=c.Operation FROM Purchasing.SupplierFinancialReceipts p
                  JOIN Purchasing.SupplierFinancialReceipts c ON c.TenantId=p.TenantId AND c.GroupId=p.GroupId AND c.RequestId<>p.RequestId
                  WHERE p.TenantId=@Tenant AND p.RequestId=@Owner;
                IF @Operation='ApplySupplierFunds' EXEC Purchasing.ApplySupplierFundsCore @ActorId,@SessionId,@Child,@Input,@Owner;
                ELSE EXEC Purchasing.RecordSupplierPaymentCore @ActorId,@SessionId,@Child,@Input,@Owner;
                COMMIT; SELECT N'{}';
              END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
            END;
            """);
        await context.Bills.AdminAsync("GRANT EXECUTE ON Purchasing.TryFinalizedSupplierComposition TO workbench_web");
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("TryFinalizedSupplierComposition", Guid.NewGuid(), new JsonObject { ["owner"] = request.ToString() }))).Number);
        Assert.Equal(finalized, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
        await context.Bills.AdminAsync($"DELETE FROM [Identity].RoleClaims WHERE ClaimValue='{(reverse ? "SupplierAllocationsManage" : "SupplierPaymentsCorrect")}'");
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync(operation, request, input))).Number);
    }

    [Fact]
    public async Task PrivateCorrectionCoresCannotBeExecutedByEitherRuntimePrincipal()
    {
        // GIVEN the production privilege model and installed private factoring boundaries.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        await using var worker = new SqlConnection(await context.Allocation.Journal.Application.CreateWorkerConnectionAsync());
        await worker.OpenAsync();
        foreach (var procedure in new[] { "Accounting.CorrectJournalCore", "Purchasing.RecordSupplierPaymentCore", "Purchasing.ApplySupplierFundsCore", "Purchasing.RequireSupplierComposition", "Purchasing.PlanSupplierCorrection", "Purchasing.PostSupplierApplicationInverse" })
        {
            // WHEN web or worker invokes a private boundary directly THEN SQL denies execution before argument parsing.
            await using var command = new SqlCommand($"EXEC {procedure}", context.Allocation.Journal.Connection);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
            await using var workerCommand = new SqlCommand($"EXEC {procedure}", worker);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => workerCommand.ExecuteNonQueryAsync())).Number);
        }
    }

    [Fact]
    public async Task PreviouslyUnappliedEmbeddedPaymentCorrectionPreservesRestoredDebt()
    {
        // GIVEN an embedded allocation already unapplied and its restored debt settled by another real payment.
        await using var prepared = await scenarios.OpenAsync("restoredDebt");
        var context = prepared.Context;
        var first = Guid.Parse(prepared.Data["first"]!.ToString());
        var second = Guid.Parse(prepared.Data["second"]!.ToString());
        var id = Guid.Parse(prepared.Data["id"]!.ToString());
        var laterPayment = prepared.Data["laterPayment"]!.AsObject();
        // WHEN the complete payment history is corrected.
        await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), prepared.Data["command"]!.AsObject());
        // THEN the combined inverse preserves later cash and settled debt; compensation alone would overdraw it.
        Assert.Equal(0m, await context.Allocation.BalanceAsync(first));
        Assert.Equal(100m, await context.Allocation.BalanceAsync(second));
        Assert.Equal(0m, await context.Allocation.BalanceAsync(id));
        Assert.Equal(-100m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierPaymentControl('{JournalTestContext.TenantId}','{laterPayment["paymentId"]}')"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context);
    }

    [Theory]
    [InlineData("80", 70)]
    public async Task ExplicitReplacementUsesOneGroupAndCorrectionAuthority(string amount, decimal remaining)
    {
        // GIVEN a real paid bill and an explicit smaller replacement; Record authority is later revoked.
        await using var prepared = await scenarios.OpenAsync("bill150");
        var context = prepared.Context;
        var bill = Guid.Parse(prepared.Data["bill"]!.GetValue<string>());
        var payment = await context.CommandAsync(); await context.AllocateAsync(payment, bill, "100");
        await context.RecordAsync(payment);
        var id = Guid.Parse(payment["paymentId"]!.ToString());
        var replacement = await context.CommandAsync(amount, "2026-09-20"); await context.AllocateAsync(replacement, bill, amount);
        var replacementId = Guid.Parse(replacement["paymentId"]!.ToString());
        var correction = await SupplierCorrectionFixture.CorrectionAsync(context, id, replacement);
        await context.Bills.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimValue='SupplierPaymentsRecord';");
        // WHEN the authorized correction owner posts the exact requested replacement.
        var result = await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), correction);
        // THEN cash reflects only the replacement, and every replacement effect belongs to the correction group.
        Assert.Equal(remaining, await context.Allocation.BalanceAsync(bill));
        Assert.Equal(0m, await context.Allocation.BalanceAsync(id));
        Assert.Equal(0m, await context.Allocation.BalanceAsync(replacementId));
        Assert.Equal(-decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
        Assert.Equal(Guid.Parse(result["groupId"]!.ToString()), await context.Bills.ScalarAsync<Guid>($"SELECT GroupId FROM Purchasing.SupplierPayments WHERE Id='{replacementId}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierPaymentControl('{JournalTestContext.TenantId}','{replacementId}')"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context);
        var nested = JsonNode.Parse(await context.Bills.ScalarAsync<string>($"SELECT RequestId requestId,CanonicalInput command,ResultJson result FROM Purchasing.SupplierFinancialReceipts WHERE GroupId='{result["groupId"]}' AND Operation='RecordSupplierPayment' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER"))!;
        var nestedRequest = Guid.Parse(nested["requestId"]!.ToString()); var nestedCommand = JsonNode.Parse(nested["command"]!.ToString())!.AsObject();
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.RecordAsync(nestedCommand, nestedRequest))).Number);
        await context.Bills.AdminAsync("INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue) SELECT TenantId,RoleId,N'workbench/permission',N'SupplierPaymentsRecord' FROM Administration.AccountingRoles WHERE Kind='Administrator';");
        Assert.Equal(nested["result"]!.ToString(), await SupplierOpenItemSecurityTests.ExecuteRawAsync(context, "RecordSupplierPayment", nestedRequest, nestedCommand));
        Assert.Equal(DateTimeOffset.Parse(result["recordedAtUtc"]!.ToString()), DateTimeOffset.Parse(JsonNode.Parse(nested["result"]!.ToString())!["recordedAtUtc"]!.ToString()));
        if (amount == "80")
        {
            // AND the replacement's genuine evidence remains usable by a later correction owner in a new group.
            await context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), await SupplierCorrectionFixture.CorrectionAsync(context, replacementId));
            Assert.Equal(150m, await context.Allocation.BalanceAsync(bill));
            Assert.Equal(0m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.Bank}'"));
            await SupplierCorrectionFixture.AssertAttributionAsync(context);
        }
    }

    [Fact]
    public async Task PreviewDriftRejectsCorrection()
    {
        // GIVEN a preview of an unapplied real payment.
        await using var context = await SupplierCorrectionFixture.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync();
        var payment = await context.CommandAsync(); await context.RecordAsync(payment);
        var id = Guid.Parse(payment["paymentId"]!.ToString());
        var command = await SupplierCorrectionFixture.CorrectionAsync(context, id);
        // WHEN another command changes its dependency graph before execution.
        await context.Allocation.ApplyAsync(await context.Allocation.CommandAsync(id, bill));
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context);
        var error = await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), command));
        // THEN rejection preserves every financial row and creates no partial inverse.
        Assert.Equal(51009, error.Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context));
    }
}

internal static class SupplierCorrectionFixture
{
    internal static async Task AssertAttributionAsync(SupplierPaymentTestContext context)
    {
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalLines l OUTER APPLY(SELECT SUM(a.Amount) Amount FROM Purchasing.SupplierControlAttributions a WHERE a.TenantId=l.TenantId AND a.JournalId=l.JournalId AND a.Ordinal=l.Ordinal AND a.AccountId=l.AccountId AND a.AccountVersion=l.AccountVersion) a WHERE l.AccountPurpose IN('SupplierPayable','SupplierAdvance','SupplierCreditReceivable','SupplierRefundClearing') AND COALESCE(a.Amount,0)<>CASE WHEN l.AccountType='Asset' THEN l.Debit-l.Credit ELSE l.Credit-l.Debit END"));
    }
    internal static async Task<SupplierPaymentTestContext> OpenAsync(SqlServerFixture fixture)
    {
        var context = await SupplierPaymentTestContext.OpenAsync(fixture);
        await context.Bills.AdminAsync("""
            GRANT EXECUTE ON Purchasing.ReverseSupplierApplication TO workbench_web;
            GRANT EXECUTE ON Purchasing.PreviewSupplierPaymentCorrection TO workbench_web;
            GRANT EXECUTE ON Purchasing.CorrectSupplierPayment TO workbench_web;
            INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
              SELECT TenantId,RoleId,N'workbench/permission',N'SupplierPaymentsCorrect'
              FROM Administration.AccountingRoles WHERE Kind='Administrator';
            """);
        return context;
    }
    internal static async Task<JsonObject> CorrectionAsync(SupplierPaymentTestContext context, Guid payment, JsonObject? replacement = null)
    {
        var command = Common(context, "CorrectSupplierPayment");
        command["paymentId"] = payment.ToString();
        command["expectedPaymentVersion"] = await context.Bills.ScalarAsync<string>($"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierPaymentVersions WHERE PaymentId='{payment}'");
        if (replacement is not null) command["replacement"] = replacement;
        var plan = await PreviewAsync(context, command);
        command["expectedPlanFingerprint"] = plan["fingerprint"]!.DeepClone();
        return command;
    }
    internal static async Task<JsonObject> PreviewAsync(SupplierPaymentTestContext context, JsonObject command)
    {
        await using var sql = new SqlCommand("EXEC Purchasing.PreviewSupplierPaymentCorrection @actor,@session,@command", context.Allocation.Journal.Connection);
        sql.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
        sql.Parameters.AddWithValue("@session", context.Allocation.Journal.SessionId);
        sql.Parameters.AddWithValue("@command", command.ToJsonString());
        return JsonNode.Parse((string)(await sql.ExecuteScalarAsync())!)!.AsObject();
    }
    internal static async Task<JsonObject> ReverseAsync(SupplierPaymentTestContext context, Guid application)
    {
        var command = Common(context, "ReverseSupplierApplication");
        command["applicationId"] = application.ToString();
        command["expectedApplicationVersion"] = await context.Bills.ScalarAsync<string>($"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierApplicationVersions WHERE ApplicationId='{application}'");
        return command;
    }
    private static JsonObject Common(SupplierPaymentTestContext context, string operation) => new()
    {
        ["schemaVersion"] = 1,
        ["operation"] = operation,
        ["expectedConfigurationVersion"] = context.Allocation.Journal.ConfigurationVersion.ToString(),
        ["purchaseOrderId"] = context.Bills.Recognition.PurchaseOrderId.ToString(),
        ["expectedPurchaseOrderVersion"] = context.Bills.Recognition.PurchaseOrderVersion,
        ["supplierId"] = context.Bills.Recognition.SupplierId.ToString(),
        ["currency"] = "USD",
        ["postingDate"] = "2026-09-20",
        ["reason"] = "Correct recorded evidence"
    };
}
