// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceStorageGuards
{
    internal static void Create(MigrationBuilder migration)
    {
        // Task 3 replaces this predicate with durable disposal-receipt authority. Never infer
        // authorization from expiry or Held alone. Ordinary PO Remove deliberately ignores it.
        migration.Sql("""
            CREATE FUNCTION Storage.FinancialEvidenceDeletionAuthorized(@TenantId uniqueidentifier,@AttachmentId uniqueidentifier)
            RETURNS bit AS BEGIN
              RETURN CASE WHEN EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks
                WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId) THEN 0 ELSE 1 END;
            END;
            """);
        migration.Sql("""
            CREATE FUNCTION Storage.FinancialEvidencePhysicalDeletionAuthorized(@TenantId uniqueidentifier,@AttachmentId uniqueidentifier)
            RETURNS bit AS BEGIN
              DECLARE @Now datetimeoffset=SYSUTCDATETIME();
              IF Storage.FinancialEvidenceDeletionAuthorized(@TenantId,@AttachmentId)=0 RETURN 0;
              IF NOT EXISTS(SELECT 1 FROM Storage.Attachments WHERE TenantId=@TenantId AND Id=@AttachmentId
                AND Held=0 AND IndependentHeld=0 AND DeletedAtUtc IS NOT NULL AND DeleteAfterUtc<=@Now) RETURN 0;
              IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId
                AND (MinimumRetentionDeadlineUtc IS NULL OR MinimumRetentionDeadlineUtc>@Now)) RETURN 0;
              RETURN 1;
            END;
            """);
        migration.Sql(RequireDeletionSql);

        migration.Sql("GRANT EXECUTE ON Storage.RequireFinancialEvidenceDeletion TO workbench_web;");
        migration.Sql("""
            CREATE TRIGGER Storage.PreserveFinancialAttachment ON Storage.Attachments AFTER UPDATE,DELETE AS BEGIN
              SET NOCOUNT ON;
              IF EXISTS(SELECT 1 FROM inserted WHERE IndependentHeld=1 AND Held=0)
                THROW 51011,'Independent attachment retention prevents deletion.',1;
              IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.TenantId=d.TenantId AND i.Id=d.Id
                WHERE Storage.FinancialEvidenceDeletionAuthorized(d.TenantId,d.Id)=0 AND
                (i.Id IS NULL OR i.Held=0
                  OR (i.DeletedAtUtc IS NOT NULL AND (d.DeletedAtUtc IS NULL OR i.DeletedAtUtc<>d.DeletedAtUtc))
                  OR (i.DeleteAfterUtc IS NOT NULL AND (d.DeleteAfterUtc IS NULL OR i.DeleteAfterUtc<>d.DeleteAfterUtc))
                  OR EXISTS(SELECT i.CurrentRevisionId EXCEPT SELECT d.CurrentRevisionId)))
                THROW 51011,'Financial evidence is retained. Use authorized retention disposal.',1;
            END;
            """);
        migration.Sql("""
            CREATE TRIGGER Storage.PreserveFinancialRevision ON Storage.Revisions AFTER UPDATE,DELETE AS BEGIN
              SET NOCOUNT ON;
              IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.TenantId=d.TenantId AND i.Id=d.Id
                WHERE EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks l WHERE l.TenantId=d.TenantId AND l.AttachmentId=d.AttachmentId)
                  AND (i.Id IS NULL OR (i.State<>d.State AND i.State IN(2,3)))
                  AND Storage.FinancialEvidencePhysicalDeletionAuthorized(d.TenantId,d.AttachmentId)=0)
                THROW 51011,'Financial evidence is retained. Use authorized retention disposal.',1;
            END;
            """);
        migration.Sql("""
            CREATE TRIGGER Purchasing.PreserveFinancialDocument ON Purchasing.PurchaseOrderDocuments AFTER UPDATE,DELETE AS BEGIN
              SET NOCOUNT ON;
              IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.TenantId=d.TenantId AND i.Id=d.Id
                WHERE Storage.FinancialEvidenceDeletionAuthorized(d.TenantId,d.AttachmentId)=0
                  AND (i.Id IS NULL OR (i.RemovedAtUtc IS NOT NULL AND (d.RemovedAtUtc IS NULL OR i.RemovedAtUtc<>d.RemovedAtUtc))
                    OR i.AttachmentId<>d.AttachmentId OR i.RevisionId<>d.RevisionId))
                THROW 51011,'Financial evidence is retained. Use authorized retention disposal.',1;
            END;
            """);
    }

    // Caller owns its transaction (and, for workers, the work-item lease lock). This command
    // keeps attachment then revision locks through provider I/O. It never takes PO/Accounting locks.
    // Task 3 supplies receipt authority in the function; physical conditions remain mandatory here.
    internal const string RequireDeletionSql = """
        CREATE PROCEDURE Storage.RequireFinancialEvidenceDeletion
          @TenantId uniqueidentifier,@AttachmentId uniqueidentifier,@PhysicalCleanup bit
        AS BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 OR @PhysicalCleanup IS NULL THROW 51011,'Deletion requires a storage transaction.',1;
          DECLARE @Attachment uniqueidentifier;
          SELECT @Attachment=Id
            FROM Storage.Attachments WITH(UPDLOCK,HOLDLOCK,INDEX(PK_Attachments)) WHERE TenantId=@TenantId AND Id=@AttachmentId;
          IF @Attachment IS NULL THROW 51011,'Attachment is unavailable.',1;
          IF Storage.FinancialEvidenceDeletionAuthorized(@TenantId,@AttachmentId)=0
            THROW 51011,'Financial evidence is retained. Use authorized retention disposal.',1;
          IF @PhysicalCleanup=1 AND Storage.FinancialEvidencePhysicalDeletionAuthorized(@TenantId,@AttachmentId)=0
            THROW 51011,'Attachment retention prevents physical deletion.',1;
          DECLARE @Revision uniqueidentifier,@Locked uniqueidentifier;
          DECLARE revision_locks CURSOR LOCAL FAST_FORWARD FOR
            SELECT Id FROM Storage.Revisions WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId ORDER BY Id;
          OPEN revision_locks; FETCH NEXT FROM revision_locks INTO @Revision;
          WHILE @@FETCH_STATUS=0
          BEGIN
            SELECT @Locked=Id FROM Storage.Revisions WITH(UPDLOCK,HOLDLOCK)
              WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId AND Id=@Revision;
            FETCH NEXT FROM revision_locks INTO @Revision;
          END;
          CLOSE revision_locks; DEALLOCATE revision_locks;
        END;
        """;

}
