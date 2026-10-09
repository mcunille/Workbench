// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using Workbench.Server.Accounting;
using Workbench.Server.Authorization;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceQueries
{
    internal static async Task<FinancialEvidenceSetResponse?> ReadAsync(WorkbenchDbContext db, RequestActor actor,
        Guid ownerId, Guid revisionId, CancellationToken ct, Guid? recognitionEventId = null)
    {
        // Supplier detail has already released its monetary report snapshot transaction. Give
        // live evidence its own boundary; journal detail reuses its existing transaction.
        await using var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
        var connection = (SqlConnection)db.Database.GetDbConnection();
        var transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await CoordinateReadAsync(db, ct);
        var set = await db.FinancialEvidenceSets.AsNoTracking().Where(s => s.OwnerRevisionId == revisionId &&
            ((s.OwnerKind == "SupplierBill" || s.OwnerKind == "SupplierPayment") && s.OwnerId == ownerId ||
             s.OwnerKind == "PurchaseRecognition" && s.OwnerId == (recognitionEventId ?? ownerId)))
            .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(ct);
        FinancialEvidenceSetResponse? result = null;
        if (set is not null)
        {
            await using var command = new SqlCommand("EXEC Accounting.ReadFinancialEvidence @actor,@session,@evidence", connection, transaction);
            command.Parameters.AddWithValue("@actor", actor.UserId);
            command.Parameters.AddWithValue("@session", actor.SessionId);
            command.Parameters.AddWithValue("@evidence", set.Value);
            result = JsonSerializer.Deserialize<FinancialEvidenceSetResponse>((string)(await command.ExecuteScalarAsync(ct))!, JsonSerializerOptions.Web);
        }
        if (owned is not null) await owned.CommitAsync(ct);
        return result;
    }
    internal static async Task CoordinateReadAsync(WorkbenchDbContext db, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        var transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using (var coordination = new SqlCommand("""
            DECLARE @result int,@resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@tenant);
            EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
            IF @result<0 THROW 51010,'Evidence read coordination is busy.',1;
            """, connection, transaction))
        {
            coordination.Parameters.AddWithValue("@tenant", db.TenantContext.RequireTenantId());
            await coordination.ExecuteNonQueryAsync(ct);
        }
    }

    internal static async Task<Dictionary<Guid, PurchaseDocumentRetentionResponse>> RetentionAsync(
        WorkbenchDbContext database, RequestActor actor, Guid[] attachments, CancellationToken ct)
    {
        var now = await database.Database.SqlQuery<DateTimeOffset>($"SELECT CONVERT(datetimeoffset,SYSUTCDATETIME()) AS Value").SingleAsync(ct);
        var links = await database.FinancialEvidenceLinks.AsNoTracking().Where(l => attachments.Contains(l.AttachmentId)).ToArrayAsync(ct);
        var states = await database.FinancialEvidenceAttachmentStates.AsNoTracking().Where(s => attachments.Contains(s.AttachmentId)).ToDictionaryAsync(s => s.AttachmentId, ct);
        var held = await database.Attachments.AsNoTracking().Where(a => attachments.Contains(a.Id) && a.IndependentHeld).Select(a => a.Id).ToArrayAsync(ct);
        var pending = await database.Database.SqlQuery<bool>($"SELECT Storage.FinancialEvidenceRecoveryPending() AS Value").SingleAsync(ct);
        return attachments.ToDictionary(id => id, id =>
        {
            var rows = links.Where(l => l.AttachmentId == id).ToArray();
            var indefinite = rows.Any(l => l.MinimumRetentionDeadlineUtc is null);
            var until = rows.Length == 0 || indefinite ? null : rows.Max(l => l.MinimumRetentionDeadlineUtc);
            var reason = rows.Length == 0 ? "This document is not financial evidence." :
                !actor.Permissions.Contains("AccountingConfigurationManage") ? "Accounting configuration permission is required." :
                held.Contains(id) ? "An independent hold protects this document." :
                pending ? "Storage recovery verification is pending." :
                indefinite ? "Retention is indefinite." : until > now ? "The retention period has not expired." : null;
            return new PurchaseDocumentRetentionResponse(rows.Length != 0, until, indefinite,
                states.TryGetValue(id, out var state) ? Convert.ToBase64String(state.RowVersion) : null, reason is null, reason);
        });
    }
}

// SQL helpers are installed only by BK-07; merged source-query definitions remain unchanged.
internal static class FinancialEvidenceReadSchema
{
    internal static void Create(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migration)
    {
        migration.Sql(BuildSql);
        migration.Sql(ReadSql);
        migration.Sql("GRANT EXECUTE ON Accounting.ReadFinancialEvidence TO workbench_web; DENY EXECUTE ON Accounting.BuildFinancialEvidence TO workbench_web; DENY EXECUTE ON Accounting.BuildFinancialEvidence TO workbench_worker;");
        FinancialEvidenceSchema.Alter(migration, "Purchasing.ReadSupplierBill",
            ("SET @Result=(SELECT b.Id billId", """
                DECLARE @FinancialSet uniqueidentifier=(SELECT Id FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND OwnerKind='SupplierBill' AND OwnerId=@BillId),@FinancialJson nvarchar(max);
                EXEC Accounting.BuildFinancialEvidence @TenantId,@FinancialSet,@FinancialJson OUTPUT;
                SET @Result=(SELECT JSON_QUERY(@FinancialJson) financialEvidence,b.Id billId
                """));
    }

    private const string BuildSql = """
        CREATE PROCEDURE Accounting.BuildFinancialEvidence @TenantId uniqueidentifier,@EvidenceSetId uniqueidentifier,@Result nvarchar(max) OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          SET @Result=NULL;
          DECLARE @Po uniqueidentifier,@Missing nvarchar(2000),@Root uniqueidentifier=@EvidenceSetId;
          SELECT @Po=s.PurchaseOrderId,@Missing=s.MissingEvidenceReason FROM Accounting.FinancialEvidenceSets s
            JOIN Purchasing.DraftOrders p ON p.TenantId=s.TenantId AND p.Id=s.PurchaseOrderId AND p.IsDeleted=0 AND p.State='Ordered'
            WHERE s.TenantId=@TenantId AND s.Id=@EvidenceSetId;
          IF @Po IS NULL RETURN;
          DECLARE @Sets TABLE(Id uniqueidentifier PRIMARY KEY);
          WHILE @EvidenceSetId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM @Sets WHERE Id=@EvidenceSetId)
          BEGIN
            IF NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@EvidenceSetId AND PurchaseOrderId=@Po) BREAK;
            INSERT @Sets VALUES(@EvidenceSetId);
            SELECT @EvidenceSetId=InheritedEvidenceSetId FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@EvidenceSetId AND PurchaseOrderId=@Po;
          END;
          SET @Result=(SELECT @Root id,@Missing missingEvidenceReason,JSON_QUERY(COALESCE((
            SELECT l.Id linkId,l.DocumentId documentId,l.RevisionId revisionId,l.Sha256 sha256,l.Length length,l.Label label,
              l.MinimumRetentionDeadlineUtc retainUntilUtc,CONVERT(bit,CASE WHEN l.MinimumRetentionDeadlineUtc IS NULL THEN 1 ELSE 0 END) indefinite,
              CASE WHEN EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceDisposalLinks dl WHERE dl.TenantId=l.TenantId AND dl.LinkId=l.Id) THEN 'Disposed'
                WHEN f.Reason IS NOT NULL THEN f.Reason
                WHEN d.RemovedAtUtc IS NOT NULL OR a.DeletedAtUtc IS NOT NULL OR r.State<>1 OR a.CurrentRevisionId<>l.RevisionId OR a.CurrentRevisionId IS NULL
                  OR Storage.FinancialEvidenceRecoveryPending()=1 THEN 'Unavailable' ELSE 'Available' END availability,
              addition.ReplacesLinkId replacesLinkId
            FROM @Sets selected JOIN Accounting.FinancialEvidenceLinks l ON l.TenantId=@TenantId AND l.EvidenceSetId=selected.Id
            JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=l.TenantId AND d.Id=l.DocumentId AND d.OrderId=@Po
            JOIN Storage.Attachments a ON a.TenantId=l.TenantId AND a.Id=l.AttachmentId
            JOIN Storage.Revisions r ON r.TenantId=l.TenantId AND r.Id=l.RevisionId AND r.AttachmentId=l.AttachmentId
            LEFT JOIN Storage.RecoveryFiles f ON f.TenantId=l.TenantId AND f.RevisionId=l.RevisionId
            LEFT JOIN Accounting.FinancialEvidenceAdditions addition ON addition.TenantId=l.TenantId AND addition.LinkId=l.Id
            ORDER BY l.RecordedAtUtc,l.Id FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]')) links
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
        END;
        """;

    private const string ReadSql = """
        CREATE PROCEDURE Accounting.ReadFinancialEvidence @ActorId uniqueidentifier,@SessionId uniqueidentifier,@EvidenceSetId uniqueidentifier
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N'AccountingReportsRead';
            DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Result nvarchar(max),@Lock int,
              @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),SESSION_CONTEXT(N'TenantId'));
            EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
            IF @Lock<0 THROW 51010,'Evidence read coordination is busy.',1;
            EXEC Accounting.BuildFinancialEvidence @Tenant,@EvidenceSetId,@Result OUTPUT;
            COMMIT; SELECT COALESCE(@Result,N'null') ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
