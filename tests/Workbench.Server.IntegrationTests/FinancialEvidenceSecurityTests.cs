// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceSecurityTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedAdditionPreservesOriginalEvidenceAndReauthorizesReplay(bool replacement)
    {
        // GIVEN a posted source, optionally with an original document, and a newly uploaded supplement.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var draft = context.CompleteDraft();
        if (replacement)
        {
            draft["revision"]!["documents"] = FinancialEvidencePostingTests.Documents(await FinancialEvidencePostingTests.UploadAsync(context, storage));
            draft["expectedPurchaseOrderVersion"] = context.Recognition.PurchaseOrderVersion;
        }
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        var owner = Guid.Parse(posted["billId"]!.GetValue<string>());
        var hash = await context.ScalarAsync<byte[]>("SELECT SourceSnapshotSha256 FROM Accounting.FinancialEvidenceSets");
        var financial = await FinancialEvidencePostingTests.FinancialHashesAsync(context);
        Guid? original = replacement ? await context.ScalarAsync<Guid>("SELECT Id FROM Accounting.FinancialEvidenceLinks") : null;
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await InstallAdapterAsync(context);
        var command = await CommandAsync(context, owner, document, original); var request = Guid.NewGuid();
        // WHEN the same supplement/replacement request is committed and replayed.
        var result = await AppendAsync(context, command, request);
        Assert.Equal(result, await AppendAsync(context, command, request));
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => AppendAsync(context, command, Guid.NewGuid()))).Number);
        // THEN history appends once and original financial bytes and protection remain unchanged.
        Assert.Equal(hash, await context.ScalarAsync<byte[]>("SELECT SourceSnapshotSha256 FROM Accounting.FinancialEvidenceSets"));
        Assert.Equal(financial, await FinancialEvidencePostingTests.FinancialHashesAsync(context));
        Assert.Equal(replacement ? 2 : 1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceAdditions"));
        Assert.Equal(replacement ? 2 : 1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Storage.Attachments WHERE Held=1"));
        if (replacement) Assert.Equal(original, await context.ScalarAsync<Guid>("SELECT ReplacesLinkId FROM Accounting.FinancialEvidenceAdditions"));
        else Assert.Equal("Reviewed invoice supplied without a file", await context.ScalarAsync<string>("SELECT MissingEvidenceReason FROM Accounting.FinancialEvidenceSets"));
        var changed = command.DeepClone().AsObject(); changed["reason"] = "Changed request reason";
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => AppendAsync(context, changed, request))).Number);
        // AND losing the original source permission denies even a successful request replay.
        await context.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimValue='SupplierBillsManage'");
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => AppendAsync(context, command, request))).Number);
    }

    [Fact]
    public async Task RuntimeCannotRewriteEvidenceClearEitherHoldOrCrossTenantBoundaries()
    {
        // GIVEN a real posted source and a restricted runtime connection.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var draft = context.CompleteDraft(); draft["revision"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        var before = await context.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceLinks FOR JSON PATH");
        // WHEN direct SQL tries to bypass internal source commands THEN no evidence or hold can be changed.
        foreach (var sql in new[]
        {
            "UPDATE Storage.Attachments SET Held=0", "UPDATE Storage.Attachments SET IndependentHeld=1",
            "UPDATE Accounting.FinancialEvidenceLinks SET RetentionYears=1", "DELETE Accounting.FinancialEvidenceLinks",
            "UPDATE Accounting.FinancialEvidenceSets SET MutationPermission='AccountingReportsRead'",
            "UPDATE Storage.FinancialEvidenceAttachmentStates SET AttachmentId=AttachmentId",
            "EXEC Accounting.CaptureFinancialEvidence 'SupplierBill',NULL,NULL,NULL",
            "EXEC Accounting.AppendFinancialEvidence NULL,NULL,NULL,N'{}'"
        })
        {
            await using var denied = new SqlCommand(sql, context.Journal.Connection);
            Assert.Equal(sql.StartsWith("UPDATE Storage.Attachments", StringComparison.Ordinal) ? 230 : 229,
                (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
        }
        foreach (var hold in new[] { "Held", "IndependentHeld" })
        {
            await using var insert = new SqlCommand($"INSERT Storage.Attachments(Id,TenantId,CreatedAtUtc,{hold}) VALUES(NEWID(),'{JournalTestContext.TenantId}',SYSUTCDATETIME(),1)", context.Journal.Connection);
            Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => insert.ExecuteNonQueryAsync())).Number);
        }
        // AND immutable metadata remains guarded even for an accidentally broadened trusted command.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.AdminAsync("UPDATE Accounting.FinancialEvidenceSets SET MissingEvidenceReason='Changed history'"))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.AdminAsync("UPDATE Purchasing.RecognitionSideEvents SET EvidenceMutationPermission='AccountingReportsRead'"))).Number);
        await using var other = await context.Journal.OpenOtherTenantAsync();
        await using var hidden = new SqlCommand("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks", other);
        Assert.Equal(0, await hidden.ExecuteScalarAsync());
        Assert.Equal(before, await context.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceLinks FOR JSON PATH"));
        Assert.True(await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
    }

    [Fact]
    public async Task ReplacementMustNameItsOwnPredecessorAndCannotInventAuthority()
    {
        // GIVEN two distinct posted bills sharing the same PO document and a later uploaded replacement.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        var firstDraft = context.CompleteDraft(); firstDraft["revision"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        var first = await SupplierBillPostingTests.ReviewedAsync(context, firstDraft);
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(first));
        var original = await context.ScalarAsync<Guid>("SELECT Id FROM Accounting.FinancialEvidenceLinks");
        var secondDraft = context.CompleteDraft("INV-2"); secondDraft["revision"]!["documents"] = FinancialEvidencePostingTests.Documents(document);
        var second = await SupplierBillPostingTests.ReviewedAsync(context, secondDraft);
        var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(second));
        var next = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await InstallAdapterAsync(context);
        var command = await CommandAsync(context, Guid.Parse(posted["billId"]!.GetValue<string>()), next, original);
        // WHEN a replacement crosses source ownership THEN no new link is created.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => AppendAsync(context, command, Guid.NewGuid()))).Number);
        command["replacesLinkId"] = null;
        command["permission"] = "AccountingReportsRead";
        // AND a caller cannot supply or substitute the source mutation permission.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => AppendAsync(context, command, Guid.NewGuid()))).Number);
        Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceReceipts"));
    }

    internal static Task InstallAdapterAsync(SupplierBillTestContext context) => context.AdminAsync("""
        EXEC(N'CREATE PROCEDURE Accounting.AppendFixtureFinancialEvidence @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
          AS BEGIN SET NOCOUNT ON; EXEC Accounting.AppendFinancialEvidence @ActorId,@SessionId,@RequestId,@Command; END;');
        GRANT EXECUTE ON Accounting.AppendFixtureFinancialEvidence TO workbench_web;
        """);

    [Fact]
    public async Task LegacyRecognitionCannotBorrowCurrentRolesToInventMutationAuthority()
    {
        // GIVEN an actual BK-06 recognition source whose schema did not retain the trusted mutation permission.
        await using var context = await SupplierBillTestContext.OpenAsync(sqlServer, "20260928071548_AddSupplierOpenItems");
        var source = await context.Recognition.CommandAsync();
        var posted = await context.Recognition.PostAsync(source.ToJsonString());
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        // AND a disposable upgrade adapter captures that authentic legacy metadata under the source lock.
        await context.AdminAsync("""
            EXEC(N'CREATE PROCEDURE Accounting.CaptureFixtureFinancialEvidence @Owner uniqueidentifier,@Revision uniqueidentifier AS BEGIN
              SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRANSACTION;
              DECLARE @Resource nvarchar(255)=N''Accounting:''+CONVERT(nvarchar(36),SESSION_CONTEXT(N''TenantId'')),@Lock int,@Set uniqueidentifier;
              EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode=''Exclusive'',@LockOwner=''Transaction'';
              EXEC Accounting.CaptureFinancialEvidence ''PurchaseRecognition'',@Owner,@Revision,@Set OUTPUT;
              COMMIT;
            END;');
            GRANT EXECUTE ON Accounting.CaptureFixtureFinancialEvidence TO workbench_web;
            """);
        await using (var capture = new SqlCommand("EXEC Accounting.CaptureFixtureFinancialEvidence @owner,@revision", context.Journal.Connection))
        {
            capture.Parameters.AddWithValue("@owner", posted.EventIds.Single());
            capture.Parameters.AddWithValue("@revision", source["units"]![0]!["sides"]![0]!["sourceRevision"]!.GetValue<string>());
            await capture.ExecuteNonQueryAsync();
        }
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await InstallAdapterAsync(context);
        var command = await CommandAsync(context, posted.EventIds.Single(), document);
        // WHEN a currently authorized source actor supplements it THEN missing historical provenance fails closed.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => AppendAsync(context, command, Guid.NewGuid()))).Number);
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.False(await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
    }

    [Fact]
    public async Task AdditionRejectsDuplicateUnknownOverlongAndEmptyEnvelopeFieldsBeforeWriting()
    {
        // GIVEN an authentic source and a valid document, with a current authorized session.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context);
        var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await InstallAdapterAsync(context);
        var valid = await CommandAsync(context, Guid.Parse(posted["billId"]!.GetValue<string>()), document);
        var empty = valid.DeepClone().AsObject(); empty["reason"] = " \t\r\n ";
        var enSpace = valid.DeepClone().AsObject(); enSpace["reason"] = "\u2002";
        var verticalTab = valid.DeepClone().AsObject(); verticalTab["reason"] = "\u000B";
        var longReason = valid.DeepClone().AsObject(); longReason["reason"] = new string('x', 2001);
        var unknown = valid.DeepClone().AsObject(); unknown["extra"] = 1;
        var huge = valid.DeepClone().AsObject(); huge["reason"] = new string('x', 131073);
        var duplicate = valid.ToJsonString().Insert(1, "\"schemaVersion\":1,");
        // WHEN malformed envelopes reach the real command THEN they produce validation failures, not partial additions.
        foreach (var input in new[] { empty.ToJsonString(), enSpace.ToJsonString(), verticalTab.ToJsonString(), longReason.ToJsonString(), unknown.ToJsonString(), huge.ToJsonString(), duplicate })
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => AppendRawAsync(context, input, Guid.NewGuid()))).Number);
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceAdditions"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceReceipts"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Storage.Attachments WHERE Held=1 OR IndependentHeld=1"));
        // AND the valid control proves those failures were validation-specific.
        await AppendAsync(context, valid, Guid.NewGuid());
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.FinancialEvidenceLinks"));
    }

    internal static async Task<JsonObject> CommandAsync(SupplierBillTestContext context, Guid owner, (Guid Document, Guid Revision) document, Guid? replaces = null)
    {
        var source = JsonNode.Parse(await context.ScalarAsync<string>($"SELECT OwnerKind ownerKind,OwnerId ownerId,OwnerRevisionId ownerRevisionId,CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) expectedEvidenceVersion FROM Accounting.FinancialEvidenceSets WHERE OwnerId='{owner}' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER"))!.AsObject();
        source["schemaVersion"] = 1; source["documentId"] = document.Document.ToString(); source["revisionId"] = document.Revision.ToString();
        source["replacesLinkId"] = replaces?.ToString(); source["reason"] = "Supporting evidence received after posting";
        return source;
    }

    internal static async Task<string> AppendAsync(SupplierBillTestContext context, JsonObject command, Guid request)
        => await AppendRawAsync(context, command.ToJsonString(), request);

    private static async Task<string> AppendRawAsync(SupplierBillTestContext context, string command, Guid request)
    {
        await using var sql = new SqlCommand("EXEC Accounting.AppendFixtureFinancialEvidence @actor,@session,@request,@command", context.Journal.Connection);
        sql.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); sql.Parameters.AddWithValue("@session", context.Journal.SessionId);
        sql.Parameters.AddWithValue("@request", request); sql.Parameters.AddWithValue("@command", command);
        return (string)(await sql.ExecuteScalarAsync())!;
    }
}
