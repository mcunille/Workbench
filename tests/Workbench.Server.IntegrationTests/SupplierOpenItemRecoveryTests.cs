// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierOpenItemRecoveryTests(SqlServerFixture sqlServer, SupplierScenarioFixture scenarios) : IClassFixture<SupplierScenarioFixture>
{
    [Fact]
    public async Task UpgradeAndRestorePreserveSupplierFinancialHistory()
    {
        // GIVEN real BK-05 bill and non-bill correction histories on the merged predecessor schema.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer, "20260928034802_AddSupplierBills");
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context);
        var billCommand = context.PostCommand(reviewed); var billRequest = Guid.NewGuid();
        var billReceipt = await context.ExecuteAsync("PostSupplierBill", billRequest, billCommand);
        var invoice = await context.Recognition.CommandAsync("Invoice", cost: "40"); var recognitionRequest = Guid.NewGuid();
        var recognitionReceipt = await context.Recognition.PostAsync(invoice.ToJsonString(), recognitionRequest);
        var beforeCorrection = await context.ScalarAsync<DateTimeOffset>("SELECT SYSDATETIMEOFFSET()");
        await context.Recognition.CorrectAsync((await PurchaseRecognitionCorrectionTests.CorrectionAsync(context.Recognition, invoice, "30")).ToJsonString());
        await PostOtherTenantHistoryAsync(context);
        var before = await SupplierOpenItemMigrationTests.SnapshotAsync(context.Recognition);
        // WHEN upgrading and then restoring the upgraded database through the guarded sanitation sequence.
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        Assert.Equal(before, await SupplierOpenItemMigrationTests.SnapshotAsync(context.Recognition));
        await MigrationHistoryAssertions.AssertCurrentAsync(context.Journal.Application.AdminConnectionString);
        var beforeRestore = await SupplierSnapshotAsync(context);
        await RestoreAsync(context);
        // THEN both cutoff balances, all immutable bytes, and original authorized retries survive.
        Assert.Equal(before, await SupplierOpenItemMigrationTests.SnapshotAsync(context.Recognition));
        Assert.Equal(beforeRestore, await SupplierSnapshotAsync(context));
        Assert.Equal("150.00", (await ReadAsync(context, "?postingThrough=2026-02-28")).Controls.WholeFilterTotals.Payable);
        Assert.Equal("150.00", (await ReadAsync(context, $"?recordedThrough={Uri.EscapeDataString(beforeCorrection.ToUniversalTime().ToString("O"))}")).Controls.WholeFilterTotals.Payable);
        var current = await ReadAsync(context);
        Assert.True(current.IsComplete); Assert.Equal("140.00", current.Controls.WholeFilterTotals.Payable);
        Assert.Equal(billReceipt.ToJsonString(), (await context.ExecuteAsync("PostSupplierBill", billRequest, billCommand)).ToJsonString());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(recognitionReceipt), System.Text.Json.JsonSerializer.Serialize(await context.Recognition.PostAsync(invoice.ToJsonString(), recognitionRequest)));
        Assert.Equal(beforeRestore, await SupplierSnapshotAsync(context));
        await using var other = await context.Journal.OpenOtherTenantAsync();
        await using var sql = new SqlCommand("SELECT COUNT(*) FROM Purchasing.SupplierItemMovements", other);
        Assert.Equal(0, await sql.ExecuteScalarAsync());
        sql.CommandText = "SELECT COUNT(*) FROM Accounting.JournalEntries";
        Assert.Equal(1, await sql.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RestoreRetainsReversedApplicationsAndUnavailableDocumentEvidence()
    {
        // GIVEN a real payment, application and inverse, with stored document metadata whose bytes are unavailable.
        await using var prepared = await scenarios.OpenAsync("recovery");
        var context = prepared.Context;
        var document = Guid.Parse(prepared.Data["document"]!.ToString());
        var revision = Guid.Parse(prepared.Data["revision"]!.ToString());
        var payment = prepared.Data["payment"]!.AsObject();
        var request = Guid.Parse(prepared.Data["request"]!.ToString());
        var receipt = prepared.Data["receipt"]!.AsObject();
        var reverse = prepared.Data["reverse"]!.AsObject();
        var reversalRequest = Guid.Parse(prepared.Data["reversalRequest"]!.ToString());
        var reversed = prepared.Data["reversed"]!.AsObject();
        var before = await SupplierSnapshotAsync(context.Bills);
        // WHEN restoring and accepting the independently missing revision through the storage recovery boundary.
        await RestoreAsync(context.Bills, revision);
        // THEN financial evidence and exact replay survive; unavailable bytes do not become available by restoring SQL.
        Assert.Equal(before, await SupplierSnapshotAsync(context.Bills));
        // AND storage itself enforces one inverse per application, independently of command/version checks.
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => context.Bills.AdminAsync("""
            INSERT Purchasing.SupplierApplicationReversals(TenantId,Id,GroupId,ApplicationId,PostingDate,Reason,ActorId,RecordedAtUtc)
              SELECT TenantId,NEWID(),GroupId,ApplicationId,PostingDate,Reason,ActorId,RecordedAtUtc FROM Purchasing.SupplierApplicationReversals;
            """));
        Assert.Equal(2601, duplicate.Number);
        Assert.Equal(before, await SupplierSnapshotAsync(context.Bills));
        Assert.Equal("Missing", await context.Bills.ScalarAsync<string>($"SELECT Reason FROM Storage.RecoveryFiles WHERE RevisionId='{revision}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document}' AND RevisionId='{revision}'"));
        var prior = await ReadAsync(context.Bills, "?postingThrough=2026-09-16");
        var current = await ReadAsync(context.Bills);
        Assert.True(prior.IsComplete); Assert.True(current.IsComplete);
        Assert.Equal(("40.00", "90.00"), (prior.Controls.WholeFilterTotals.Advance, prior.Controls.WholeFilterTotals.Payable));
        Assert.Equal(("100.00", "150.00"), (current.Controls.WholeFilterTotals.Advance, current.Controls.WholeFilterTotals.Payable));
        Assert.Equal(receipt.ToJsonString(), (await context.RecordAsync(payment, request)).ToJsonString());
        Assert.Equal(reversed.ToJsonString(), (await context.Bills.ExecuteAsync("ReverseSupplierApplication", reversalRequest, reverse)).ToJsonString());
        var newPayment = await context.CommandAsync(); newPayment["evidence"] = payment["evidence"]!.DeepClone();
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.RecordAsync(newPayment))).Number);
        Assert.Equal(before, await SupplierSnapshotAsync(context.Bills));
    }

    [Fact]
    public async Task RebuildRestoresSupportedAttribution()
    {
        // GIVEN supported bill, invoice and correction sources whose reports reconcile before projection loss.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context);
        var command = context.PostCommand(reviewed); var request = Guid.NewGuid();
        var receipt = await context.ExecuteAsync("PostSupplierBill", request, command);
        var invoice = await context.Recognition.CommandAsync("Invoice", cost: "40");
        await context.Recognition.PostAsync(invoice.ToJsonString());
        await context.Recognition.CorrectAsync((await PurchaseRecognitionCorrectionTests.CorrectionAsync(context.Recognition, invoice, "30")).ToJsonString());
        Assert.True((await ReadAsync(context)).IsComplete);
        var before = await SupplierSnapshotAsync(context);
        var attribution = await context.ScalarAsync<string>("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(SELECT * FROM Purchasing.SupplierControlAttributions ORDER BY TenantId,Id FOR JSON PATH)),2)");
        // WHEN an operator loses only derived attribution in this disposable database, then rebuilds from source evidence.
        await context.AdminAsync("DELETE Purchasing.SupplierControlAttributions");
        Assert.False((await ReadAsync(context)).IsComplete);
        // AND a correction-owner link can be detached while retaining a valid source-event foreign key.
        await context.AdminAsync("SELECT Id,OriginalSourceEventId,ReversalSourceEventId INTO Purchasing.RecoveryCorrectionBackup FROM Accounting.CorrectionGroups;");
        foreach (var column in new[] { "ReversalSourceEventId", "OriginalSourceEventId" })
        {
            var otherColumn = column == "ReversalSourceEventId" ? "OriginalSourceEventId" : "ReversalSourceEventId";
            await context.AdminAsync($"UPDATE Accounting.CorrectionGroups SET {column}={otherColumn};");
            try
            {
                await DeriveAsync(context);
                // THEN only the two independent original groups can recover; the detached correction stays unresolved.
                Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions"));
                Assert.False((await ReadAsync(context)).IsComplete);
            }
            finally
            {
                await context.AdminAsync($"UPDATE target SET {column}=original.{column} FROM Accounting.CorrectionGroups target JOIN Purchasing.RecoveryCorrectionBackup original ON original.Id=target.Id;");
            }
        }
        // AND the original source row can lose recognition ownership without breaking any foreign key.
        await context.AdminAsync("""
            SELECT source.Id,source.SourceKind,source.SourceId,source.SourceRevision INTO Purchasing.RecoverySourceBackup
              FROM Accounting.SourceEvents source JOIN Accounting.CorrectionGroups correction
                ON correction.TenantId=source.TenantId AND correction.OriginalSourceEventId=source.Id;
            """);
        foreach (var (column, changed) in new[] { ("SourceRevision", "NEWID()"), ("SourceId", "NEWID()"), ("SourceKind", "N'Unsupported'") })
        {
            await context.AdminAsync($"DELETE Purchasing.SupplierControlAttributions; UPDATE source SET {column}={changed} FROM Accounting.SourceEvents source JOIN Purchasing.RecoverySourceBackup original ON original.Id=source.Id;");
            try
            {
                await DeriveAsync(context);
                // THEN only the independent bill can recover; neither the unknown original nor its correction is supported.
                Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions"));
                Assert.False((await ReadAsync(context)).IsComplete);
            }
            finally
            {
                await context.AdminAsync($"UPDATE source SET {column}=original.{column} FROM Accounting.SourceEvents source JOIN Purchasing.RecoverySourceBackup original ON original.Id=source.Id;");
            }
            // AND restoring that exact source identity restores all four original attribution rows.
            await DeriveAsync(context);
            Assert.Equal(attribution, await context.ScalarAsync<string>("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(SELECT * FROM Purchasing.SupplierControlAttributions ORDER BY TenantId,Id FOR JSON PATH)),2)"));
            Assert.Equal(before, await SupplierSnapshotAsync(context));
            Assert.True((await ReadAsync(context)).IsComplete);
        }
        await DeriveAsync(context);
        // THEN exact deterministic attribution and historical totals return without changing immutable money or receipts.
        Assert.True((await ReadAsync(context)).IsComplete);
        Assert.Equal(attribution, await context.ScalarAsync<string>("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(SELECT * FROM Purchasing.SupplierControlAttributions ORDER BY TenantId,Id FOR JSON PATH)),2)"));
        Assert.Equal(before, await SupplierSnapshotAsync(context));
        Assert.Equal("150.00", (await ReadAsync(context, "?postingThrough=2026-02-28")).Controls.WholeFilterTotals.Payable);
        Assert.Equal("140.00", (await ReadAsync(context)).Controls.WholeFilterTotals.Payable);
        await DeriveAsync(context);
        Assert.Equal(attribution, await context.ScalarAsync<string>("SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(SELECT * FROM Purchasing.SupplierControlAttributions ORDER BY TenantId,Id FOR JSON PATH)),2)"));
        Assert.Equal(receipt.ToJsonString(), (await context.ExecuteAsync("PostSupplierBill", request, command)).ToJsonString());
        Assert.Equal(before, await SupplierSnapshotAsync(context));
    }

    [Fact]
    public async Task RebuildDoesNotRepairConflictingOrDetachedEvidence()
    {
        // GIVEN two monetary lines in one authentic bill group; intact attribution is the positive control.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var draft = context.CompleteDraft();
        var second = draft["revision"]!["units"]![0]!.DeepClone();
        second["unitId"] = Guid.NewGuid().ToString(); second["componentKey"] = "line-2";
        draft["revision"]!["units"]!.AsArray().Add(second); draft["revision"]!["total"] = "220";
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        Assert.True((await ReadAsync(context)).IsComplete);
        var before = await SupplierSnapshotAsync(context);
        // WHEN only one derived row is missing but another conflicts, the entire group must remain unresolved.
        await context.AdminAsync("SELECT * INTO Purchasing.RecoveryAttributionBackup FROM Purchasing.SupplierControlAttributions; DELETE TOP(1) FROM Purchasing.SupplierControlAttributions; UPDATE Purchasing.SupplierControlAttributions SET Amount=Amount+1;");
        await DeriveAsync(context);
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions"));
        Assert.False((await ReadAsync(context)).IsComplete);
        await context.AdminAsync("DELETE Purchasing.SupplierControlAttributions;");
        var faults = new[]
        {
            ("SupplierFinancialGroups", "SourceId", "NEWID()"),
            ("SupplierFinancialGroups", "RecordedAtUtc", "DATEADD(second,1,RecordedAtUtc)"),
            ("SupplierOpenItems", "SourceId", "NEWID()"),
            ("SupplierOpenItems", "SourceRevisionId", "NEWID()"),
            ("SupplierOpenItems", "SourceSnapshotJson", "N'{}'"),
            ("SupplierItemMovements", "SourceEventId", "(SELECT TOP(1) Id FROM Accounting.SourceEvents ORDER BY Id)"),
            ("SupplierItemMovements", "PostingDate", "DATEADD(day,1,PostingDate)")
        };
        foreach (var (table, column, changed) in faults)
        {
            // AND a privileged probe detaches one immutable identity while preserving the monetary totals.
            await context.AdminAsync($"SELECT Id,{column} INTO Purchasing.RecoveryOwnerBackup FROM Purchasing.{table}; UPDATE Purchasing.{table} SET {column}={changed};");
            try
            {
                await DeriveAsync(context);
                // THEN no missing attribution is invented from amounts alone.
                Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions"));
                Assert.False((await ReadAsync(context)).IsComplete);
            }
            finally
            {
                await context.AdminAsync($"UPDATE target SET {column}=original.{column} FROM Purchasing.{table} target JOIN Purchasing.RecoveryOwnerBackup original ON original.Id=target.Id; DROP TABLE Purchasing.RecoveryOwnerBackup;");
            }
        }
        // AND restoring the original ownership makes precisely the original derived rows recoverable again.
        await DeriveAsync(context);
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM (SELECT * FROM Purchasing.RecoveryAttributionBackup EXCEPT SELECT * FROM Purchasing.SupplierControlAttributions) delta"));
        Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions"));
        Assert.True((await ReadAsync(context)).IsComplete);
        Assert.Equal(before, await SupplierSnapshotAsync(context));
    }

    [Fact]
    public async Task UnknownLegacyControlRemainsUnresolved()
    {
        // GIVEN a supported BK-05 invoice plus a journal whose source identity cannot establish attribution.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer, "20260928034802_AddSupplierBills");
        var valid = await context.Recognition.CommandAsync("Invoice", cost: "40");
        await context.Recognition.PostAsync(valid.ToJsonString());
        var unknown = await context.Recognition.CommandAsync("Invoice", cost: "70");
        var posted = await context.Recognition.PostAsync(unknown.ToJsonString());
        await context.AdminAsync($"UPDATE Accounting.SourceEvents SET SourceRevision=NEWID() WHERE Id=(SELECT SourceEventId FROM Accounting.JournalEntries WHERE Id='{posted.JournalIds[0]}')");
        var before = await SupplierOpenItemMigrationTests.SnapshotAsync(context.Recognition);
        // WHEN migration and a later derivation run encounter this evidence.
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        await DeriveAsync(context);
        // THEN the unsupported 70 remains visible and incomplete; no guessed capacity or history rewriting hides it.
        var report = await ReadAsync(context);
        Assert.False(report.IsComplete); Assert.True(report.UnresolvedTenantControlCount > 0);
        Assert.Equal("40.00", report.Controls.WholeFilterTotals.Payable);
        Assert.Contains(report.Controls.Items, c => c.MissingAttributionCount > 0);
        Assert.Equal(before, await SupplierOpenItemMigrationTests.SnapshotAsync(context.Recognition));
    }

    [Fact]
    public async Task DownMigrationCannotEraseSupplierHistory()
    {
        // GIVEN durable supplier payment and application history on the current schema.
        await using var context = await SupplierPaymentTestContext.OpenAsync(sqlServer);
        var bill = await context.Allocation.BillAsync(); var payment = await context.CommandAsync();
        await context.AllocateAsync(payment, bill, "100"); await context.RecordAsync(payment);
        var before = await SupplierSnapshotAsync(context.Bills);
        // WHEN destructive downgrade is requested THEN its explicit guard rejects before any evidence is erased.
        var error = await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(context.Allocation.Journal.Application.AdminConnectionString, "20260928034802_AddSupplierBills", default));
        Assert.Equal(50020, error.Number);
        Assert.Equal(before, await SupplierSnapshotAsync(context.Bills));
        await MigrationHistoryAssertions.AssertCurrentAsync(context.Allocation.Journal.Application.AdminConnectionString);
        Assert.True((await ReadAsync(context.Bills)).IsComplete);
    }

    internal static Task DeriveAsync(SupplierBillTestContext context) => context.AdminAsync("BEGIN TRY BEGIN TRAN; " + SupplierOpenItemBackfill.Sql + " COMMIT; END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;");

    private static async Task PostOtherTenantHistoryAsync(SupplierBillTestContext context)
    {
        // Independent tenant setup supplies no receipt or journal; the real kernel records its source below.
        var session = Guid.NewGuid(); var source = Guid.NewGuid(); var revision = Guid.NewGuid();
        var debit = Guid.NewGuid(); var credit = Guid.NewGuid(); var version = Guid.NewGuid();
        await context.AdminAsync($"""
            INSERT [Identity].UserRoles(TenantId,UserId,RoleId)
              SELECT '{JournalTestContext.OtherTenantId}','{AuthTestApplication.OtherTenantUserId}',RoleId FROM Administration.AccountingRoles
              WHERE TenantId='{JournalTestContext.OtherTenantId}' AND Kind='Administrator';
            INSERT [Identity].Sessions(Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
              SELECT '{session}',TenantId,Id,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME())
              FROM [Identity].Users WHERE Id='{AuthTestApplication.OtherTenantUserId}';
            INSERT Accounting.Configurations(TenantId,Payload,Version)
              SELECT '{JournalTestContext.OtherTenantId}',JSON_MODIFY(Payload,'$.mappings',JSON_QUERY('[]')),'{version}'
              FROM Accounting.Configurations WHERE TenantId='{JournalTestContext.TenantId}';
            INSERT Accounting.Accounts(Id,TenantId,Code,Name,Type,Purpose,Version)
              VALUES('{debit}','{JournalTestContext.OtherTenantId}','OTHER-D','Other asset','Asset','General','{version}'),
                    ('{credit}','{JournalTestContext.OtherTenantId}','OTHER-C','Other liability','Liability','General','{version}');
            INSERT Accounting.SyntheticSources(TenantId,Id,Revision,Amount,Currency,DebitAccountId,CreditAccountId)
              VALUES('{JournalTestContext.OtherTenantId}','{source}','{revision}',17,'USD','{debit}','{credit}');
            """);
        await using var other = await context.Journal.OpenOtherTenantAsync();
        await context.Journal.PostAsync(new JournalTestContext.SyntheticSource(source, revision, "17", "USD"), connection: other,
            actorId: AuthTestApplication.OtherTenantUserId, sessionId: session, expectedConfigurationVersion: version,
            expectedDebitVersion: version, expectedCreditVersion: version);
        Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(DISTINCT TenantId) FROM Accounting.JournalEntries"));
    }

    internal static async Task<SupplierReconciliationSummary> ReadAsync(SupplierBillTestContext context, string query = "")
    {
        await using var connection = await context.Journal.OpenSiblingAsync();
        await using var db = new WorkbenchDbContext(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlServer(connection).Options, new TenantContext(JournalTestContext.TenantId));
        var http = new DefaultHttpContext(); http.Request.QueryString = new QueryString(query);
        var result = await SupplierReconciliationQueries.Reconcile(http, db, new EphemeralDataProtectionProvider(), default);
        return Assert.IsType<SupplierReconciliationSummary>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    }

    private static async Task<string> SupplierSnapshotAsync(SupplierBillTestContext context) =>
        await SupplierOpenItemMigrationTests.SnapshotAsync(context.Recognition) + await context.ScalarAsync<string>("""
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT(
              (SELECT * FROM Purchasing.SupplierOpenItems ORDER BY TenantId,Id FOR JSON PATH),
              (SELECT * FROM Purchasing.SupplierItemMovements ORDER BY TenantId,Id FOR JSON PATH),
              (SELECT * FROM Purchasing.SupplierControlAttributions ORDER BY TenantId,Id FOR JSON PATH),
              (SELECT * FROM Purchasing.SupplierFinancialGroups ORDER BY TenantId,Sequence FOR JSON PATH),
              (SELECT * FROM Purchasing.SupplierFinancialReceipts ORDER BY TenantId,RequestId FOR JSON PATH),
              (SELECT * FROM Purchasing.SupplierPayments ORDER BY TenantId,Id FOR JSON PATH),
              (SELECT * FROM Purchasing.SupplierPaymentCorrections ORDER BY TenantId,Id FOR JSON PATH),
              (SELECT * FROM Purchasing.SupplierApplications ORDER BY TenantId,Id FOR JSON PATH),
              (SELECT * FROM Purchasing.SupplierApplicationReversals ORDER BY TenantId,Id FOR JSON PATH),
              (SELECT * FROM [Identity].RoleClaims ORDER BY Id FOR JSON PATH),
              (SELECT * FROM sys.database_permissions ORDER BY class,major_id,minor_id,grantee_principal_id,permission_name FOR JSON PATH))),2)
            """);

    private static async Task RestoreAsync(SupplierBillTestContext context, Guid? missingRevision = null)
    {
        var target = new SqlConnectionStringBuilder(context.Journal.Application.AdminConnectionString) { Pooling = false };
        var database = target.InitialCatalog;
        Assert.Matches("^workbench_test_[a-f0-9]+$", database);
        var path = $"/var/opt/mssql/data/{database}.bak";
        await using (var backup = new SqlConnection(target.ConnectionString))
        {
            await backup.OpenAsync();
            await using var command = new SqlCommand($"BACKUP DATABASE [{database}] TO DISK=@path WITH COPY_ONLY,CHECKSUM,INIT", backup);
            command.Parameters.AddWithValue("@path", path); await command.ExecuteNonQueryAsync();
        }
        await context.Journal.Connection.CloseAsync();
        var master = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" };
        await using (var restore = new SqlConnection(master.ConnectionString))
        {
            await restore.OpenAsync();
            await using var command = new SqlCommand($"""
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                RESTORE DATABASE [{database}] FROM DISK=@path WITH REPLACE,CHECKSUM,RECOVERY,RESTRICTED_USER;
                USE [{database}]; EXEC Administration.MarkRestorePending;
                """, restore) { CommandTimeout = 120 };
            command.Parameters.AddWithValue("@path", path); await command.ExecuteNonQueryAsync();
        }
        await using (var proof = new SqlConnection(target.ConnectionString))
        {
            await proof.OpenAsync();
            await using var command = new SqlCommand("SELECT IsPending FROM Security.WorkbenchRestorePending", proof);
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT user_access_desc FROM sys.databases WHERE name=DB_NAME()";
            Assert.Equal("RESTRICTED_USER", await command.ExecuteScalarAsync());
            command.CommandText = "EXEC Administration.SanitizeRestore @Now=@now,@CorrelationId=N'bk06-disposable-recovery'";
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow); await command.ExecuteNonQueryAsync();
            command.CommandText = "SELECT COUNT(*) FROM [Identity].Sessions"; Assert.Equal(0, await command.ExecuteScalarAsync());
            if (missingRevision is not null)
            {
                command.CommandText = """
                BEGIN TRY BEGIN TRAN;
                  DECLARE @inventory nvarchar(max),@generation bigint,@fingerprint varchar(64),@report uniqueidentifier=NEWID();
                  EXEC Storage.ReadRecoveryInventory @inventory OUTPUT;
                  SET @generation=TRY_CONVERT(bigint,JSON_VALUE(@inventory,'$.Generation'));
                  SET @fingerprint=CONVERT(varchar(64),HASHBYTES('SHA2_256',@inventory),2);
                  EXEC Storage.AcceptFileRecovery @report,@generation,@fingerprint,N'local',@missing;
                  COMMIT;
                END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
                """;
                command.Parameters.AddWithValue("@missing", missingRevision is null ? "[]" : new JsonArray(new JsonObject { ["TenantId"] = JournalTestContext.TenantId.ToString(), ["RevisionId"] = missingRevision.ToString(), ["Reason"] = "Missing" }).ToJsonString());
                await command.ExecuteNonQueryAsync();
            }
            command.CommandText = "SELECT IsPending FROM Security.WorkbenchRestorePending"; Assert.False((bool)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT IsPending FROM Security.BlobRecoveryState"; Assert.False((bool)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT ProofKey FROM Security.TenantContextKeys WHERE Id=1";
            ((byte[])(await command.ExecuteScalarAsync())!).CopyTo(context.Journal.ProofKey, 0);
            command.CommandText = $"USE master; ALTER DATABASE [{database}] SET MULTI_USER"; await command.ExecuteNonQueryAsync();
        }
        // The original restored session is invalidated; establish fresh fixture authentication before authorized replay.
        await context.AdminAsync($"""
            INSERT [Identity].Sessions(Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
              SELECT '{context.Journal.SessionId}',TenantId,Id,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME())
              FROM [Identity].Users WHERE Id='{JournalTestContext.ActorId}';
            """);
        await context.Journal.Connection.OpenAsync();
        await new TenantContextProof(context.Journal.ProofKey).ApplyAsync(context.Journal.Connection, JournalTestContext.TenantId, default);
    }
}
