// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceDisposal
{
    internal static void Create(MigrationBuilder migration)
    {
        migration.Sql("""
            CREATE FUNCTION Storage.FinancialEvidenceRecoveryPending() RETURNS bit AS BEGIN
              RETURN CASE WHEN EXISTS(SELECT 1 FROM Security.BlobRecoveryState WHERE IsPending=1)
                OR EXISTS(SELECT 1 FROM Security.WorkbenchRestorePending WHERE IsPending=1) THEN 1 ELSE 0 END;
            END;
            """);
        migration.Sql("GRANT EXECUTE ON Storage.FinancialEvidenceRecoveryPending TO workbench_web;");
        migration.Sql("""
            CREATE FUNCTION Storage.FinancialEvidenceRetentionElapsed(@TenantId uniqueidentifier,@AttachmentId uniqueidentifier,@AtUtc datetimeoffset)
            RETURNS bit AS BEGIN
              IF @AtUtc IS NULL OR EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks
                WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId
                  AND (MinimumRetentionDeadlineUtc IS NULL OR MinimumRetentionDeadlineUtc>@AtUtc)) RETURN 0;
              RETURN 1;
            END;
            """);
        migration.Sql("DENY EXECUTE ON Storage.FinancialEvidenceRetentionElapsed TO workbench_web; DENY EXECUTE ON Storage.FinancialEvidenceRetentionElapsed TO workbench_worker;");
        migration.Sql("""
            ALTER FUNCTION Storage.FinancialEvidencePhysicalDeletionAuthorized(@TenantId uniqueidentifier,@AttachmentId uniqueidentifier)
            RETURNS bit AS BEGIN
              DECLARE @Now datetimeoffset=SYSUTCDATETIME();
              IF Storage.FinancialEvidenceDeletionAuthorized(@TenantId,@AttachmentId)=0 RETURN 0;
              IF NOT EXISTS(SELECT 1 FROM Storage.Attachments WHERE TenantId=@TenantId AND Id=@AttachmentId
                AND Held=0 AND IndependentHeld=0 AND DeletedAtUtc IS NOT NULL AND DeleteAfterUtc<=@Now) RETURN 0;
              IF Storage.FinancialEvidenceRetentionElapsed(@TenantId,@AttachmentId,@Now)=0 RETURN 0;
              RETURN 1;
            END;
            """);
        FinancialEvidenceSchema.Alter(migration, "Purchasing.PreparePurchaseOrderDocument",
            ("IF EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocumentOperations WHERE TenantId=@TenantId AND RequestId=@RequestId)", """
                IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId AND Operation='Dispose')
                  THROW 50077,'Use the authorized disposal operation to recover this request.',1;
                IF EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocumentOperations WHERE TenantId=@TenantId AND RequestId=@RequestId)
                """));
        foreach (var table in new[] { "FinancialEvidenceDisposals", "FinancialEvidenceDisposalLinks" })
        {
            migration.Sql($"""
                ALTER SECURITY POLICY Security.TenantIsolationPolicy
                  ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Accounting.{table},
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Accounting.{table} AFTER INSERT,
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Accounting.{table} AFTER UPDATE;
                GRANT SELECT ON Accounting.{table} TO workbench_web;
                DENY INSERT,UPDATE,DELETE ON Accounting.{table} TO workbench_web;
                DENY INSERT,UPDATE,DELETE ON Accounting.{table} TO workbench_worker;
                """);
            migration.Sql($"CREATE TRIGGER Accounting.Preserve{table} ON Accounting.{table} AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM deleted) THROW 51009,'Disposal evidence is immutable.',1; END;");
        }
        migration.Sql("""
            ALTER TABLE Accounting.FinancialEvidenceDisposals ADD CONSTRAINT FK_FinancialEvidenceDisposals_Operation
              FOREIGN KEY(TenantId,RequestId) REFERENCES Purchasing.PurchaseOrderDocumentOperations(TenantId,RequestId);
            """);
        migration.Sql("""
            ALTER FUNCTION Storage.FinancialEvidenceDeletionAuthorized(@TenantId uniqueidentifier,@AttachmentId uniqueidentifier)
            RETURNS bit AS BEGIN
              IF NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId) RETURN 1;
              DECLARE @Request uniqueidentifier;
              SELECT @Request=d.RequestId FROM Accounting.FinancialEvidenceDisposals d
                JOIN Accounting.FinancialEvidenceReceipts r ON r.TenantId=d.TenantId AND r.RequestId=d.RequestId AND r.Operation='Dispose'
                WHERE d.TenantId=@TenantId AND d.AttachmentId=@AttachmentId;
              IF @Request IS NULL RETURN 0;
              IF EXISTS(SELECT Id FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId
                EXCEPT SELECT LinkId FROM Accounting.FinancialEvidenceDisposalLinks WHERE TenantId=@TenantId AND RequestId=@Request) RETURN 0;
              IF EXISTS(SELECT LinkId FROM Accounting.FinancialEvidenceDisposalLinks WHERE TenantId=@TenantId AND RequestId=@Request
                EXCEPT SELECT Id FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId) RETURN 0;
              RETURN 1;
            END;
            """);
        // Receipt authority permits the single recorded removal, never a later rewrite of identity or grace.
        migration.Sql("""
            CREATE TRIGGER Storage.BindFinancialDisposal ON Storage.Attachments AFTER UPDATE,DELETE AS BEGIN
              SET NOCOUNT ON;
              IF EXISTS(SELECT 1 FROM deleted old JOIN Accounting.FinancialEvidenceDisposals d ON d.TenantId=old.TenantId AND d.AttachmentId=old.Id
                LEFT JOIN inserted i ON i.TenantId=old.TenantId AND i.Id=old.Id
                WHERE i.Id IS NULL OR i.DeletedAtUtc IS NULL OR i.DeleteAfterUtc IS NULL OR i.DeletedAtUtc<>d.RemovedAtUtc OR i.DeleteAfterUtc<>d.DeleteAfterUtc
                  OR EXISTS(SELECT i.CurrentRevisionId EXCEPT SELECT old.CurrentRevisionId))
                THROW 51011,'The authorized financial disposal identity and grace are immutable.',1;
            END;
            """);
        migration.Sql("""
            CREATE TRIGGER Purchasing.BindFinancialDisposal ON Purchasing.PurchaseOrderDocuments AFTER UPDATE,DELETE AS BEGIN
              SET NOCOUNT ON;
              IF EXISTS(SELECT 1 FROM deleted old JOIN Accounting.FinancialEvidenceDisposals d ON d.TenantId=old.TenantId AND d.DocumentId=old.Id
                LEFT JOIN inserted i ON i.TenantId=old.TenantId AND i.Id=old.Id
                WHERE i.Id IS NULL OR i.RemovedAtUtc IS NULL OR i.RemovedAtUtc<>d.RemovedAtUtc OR i.AttachmentId<>old.AttachmentId
                  OR i.RevisionId<>old.RevisionId OR i.OrderId<>old.OrderId)
                THROW 51011,'Disposed financial document identity is immutable.',1;
            END;
            """);
        migration.Sql(DisposeSql);
        migration.Sql(ReadSql);
        migration.Sql("GRANT EXECUTE ON Purchasing.DisposeRetainedDocument TO workbench_web; GRANT EXECUTE ON Purchasing.ReadRetainedDocumentDisposal TO workbench_web;");
    }

    internal const string DisposeSql = """
        CREATE PROCEDURE Purchasing.DisposeRetainedDocument
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
          @OrderId uniqueidentifier,@DocumentId uniqueidentifier,@ExpectedOrderVersion binary(8),
          @ExpectedDocumentVersion binary(8),@ExpectedEvidenceVersion binary(8),@Reason nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N'AccountingConfigurationManage';
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
              OR @ExpectedOrderVersion IS NULL OR @ExpectedDocumentVersion IS NULL OR @ExpectedEvidenceVersion IS NULL
              OR @Reason IS NULL OR DATALENGTH(@Reason) NOT BETWEEN 2 AND 4000
              OR LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM @Reason))=0
              THROW 51000,'Provide current versions, a request and a reason of 1 to 2000 characters.',1;
            DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Lock int,
              @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),SESSION_CONTEXT(N'TenantId')),
              @OrderVersion binary(8),@DocumentVersion binary(8),@Attachment uniqueidentifier,@Revision uniqueidentifier,@Result nvarchar(max);
            EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @Lock<0 THROW 51009,'Evidence is being changed. Retry.',1;
            SELECT @OrderVersion=RowVersion FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK)
              WHERE TenantId=@Tenant AND Id=@OrderId AND IsDeleted=0 AND State='Ordered';
            IF @OrderVersion IS NULL THROW 51004,'Purchase order unavailable.',1;
            DECLARE @Canonical nvarchar(max)=(SELECT @OrderId orderId,@DocumentId documentId,
              CONVERT(varchar(18),@ExpectedOrderVersion,1) expectedOrderVersion,CONVERT(varchar(18),@ExpectedDocumentVersion,1) expectedDocumentVersion,
              CONVERT(varchar(18),@ExpectedEvidenceVersion,1) expectedEvidenceVersion,@Reason reason FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId AND Operation='Dispose'
                AND ActorId=@ActorId AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
                THROW 51009,'Request identity has different content.',1;
              SELECT @Result=ResultJson FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId;
              COMMIT; SELECT @Result ResultJson; RETURN;
            END;
            IF EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocumentOperations WHERE TenantId=@Tenant AND RequestId=@RequestId)
              THROW 51009,'Request identity is already in use.',1;
            SELECT @DocumentVersion=RowVersion,@Attachment=AttachmentId,@Revision=RevisionId FROM Purchasing.PurchaseOrderDocuments WITH(UPDLOCK,HOLDLOCK)
              WHERE TenantId=@Tenant AND OrderId=@OrderId AND Id=@DocumentId AND RemovedAtUtc IS NULL;
            IF @DocumentVersion IS NULL THROW 51004,'Document unavailable.',1;
            IF @OrderVersion<>@ExpectedOrderVersion OR @DocumentVersion<>@ExpectedDocumentVersion THROW 51009,'The purchase order or document changed.',1;
            DECLARE @Independent bit,@Deleted datetimeoffset,@Current uniqueidentifier,@Evidence binary(8),@Locked uniqueidentifier;
            SELECT @Independent=IndependentHeld,@Deleted=DeletedAtUtc,@Current=CurrentRevisionId
              FROM Storage.Attachments WITH(UPDLOCK,HOLDLOCK,INDEX(PK_Attachments)) WHERE TenantId=@Tenant AND Id=@Attachment;
            SELECT @Locked=Id FROM Storage.Revisions WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND AttachmentId=@Attachment AND Id=@Revision;
            SELECT @Evidence=RowVersion FROM Storage.FinancialEvidenceAttachmentStates WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND AttachmentId=@Attachment;
            IF @Evidence IS NULL OR @Evidence<>@ExpectedEvidenceVersion THROW 51009,'Financial evidence changed. Reload before disposal.',1;
            DECLARE @Now datetimeoffset=SYSUTCDATETIME(),@After datetimeoffset,@Operation uniqueidentifier=NEWID();
            SET @After=DATEADD(day,7,@Now);
            IF @Independent IS NULL OR @Independent=1 OR @Deleted IS NOT NULL OR @Current<>@Revision OR @Current IS NULL OR @Locked IS NULL
              OR Storage.FinancialEvidenceRecoveryPending()=1
              OR NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@Tenant AND AttachmentId=@Attachment)
              OR Storage.FinancialEvidenceRetentionElapsed(@Tenant,@Attachment,@Now)=0
              THROW 51011,'Every evidence link must have expired and all independent holds must be released before disposal.',1;
            UPDATE Purchasing.DraftOrders SET UpdatedAtUtc=@Now,UpdatedByUserId=@ActorId WHERE TenantId=@Tenant AND Id=@OrderId;
            SELECT @OrderVersion=RowVersion FROM Purchasing.DraftOrders WHERE TenantId=@Tenant AND Id=@OrderId;
            SET @Result=(SELECT @RequestId requestId,'Completed' state,@DocumentId documentId,CONVERT(varchar(18),@OrderVersion,1) orderVersion FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT Purchasing.PurchaseOrderDocumentOperations(Id,TenantId,OrderId,RequestId,DocumentId,AttachmentId,RevisionId,ActorUserId,Kind,State,ExpectedOrderVersion,ExpectedDocumentVersion,ResultOrderVersion,CreatedAtUtc)
              VALUES(@Operation,@Tenant,@OrderId,@RequestId,@DocumentId,@Attachment,@Revision,@ActorId,2,1,@ExpectedOrderVersion,@ExpectedDocumentVersion,@OrderVersion,@Now);
            INSERT Accounting.FinancialEvidenceReceipts(TenantId,RequestId,Operation,ActorId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
              VALUES(@Tenant,@RequestId,'Dispose',@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);
            INSERT Accounting.FinancialEvidenceDisposals(TenantId,RequestId,AttachmentId,DocumentId,RemovedAtUtc,DeleteAfterUtc)
              VALUES(@Tenant,@RequestId,@Attachment,@DocumentId,@Now,@After);
            INSERT Accounting.FinancialEvidenceDisposalLinks(TenantId,RequestId,LinkId)
              SELECT @Tenant,@RequestId,Id FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@Tenant AND AttachmentId=@Attachment;
            UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@DocumentId;
            UPDATE Storage.Attachments SET Held=0,DeletedAtUtc=@Now,DeleteAfterUtc=@After WHERE TenantId=@Tenant AND Id=@Attachment;
            INSERT Operations.WorkItems(Id,TenantId,Kind,AttachmentId,State,Attempts,Generation,CreatedAtUtc,AvailableAtUtc)
              VALUES(NEWID(),@Tenant,1,@Attachment,0,0,0,@Now,@After);
            INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
              VALUES(NEWID(),@Tenant,@ActorId,'purchasing.document.disposed','PurchaseOrder',@OrderId,@Now);
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;

    private const string ReadSql = """
        CREATE PROCEDURE Purchasing.ReadRetainedDocumentDisposal @ActorId uniqueidentifier,@SessionId uniqueidentifier,
          @OrderId uniqueidentifier,@RequestId uniqueidentifier
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N'AccountingConfigurationManage';
            DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Result nvarchar(max);
            SELECT @Result=r.ResultJson FROM Accounting.FinancialEvidenceReceipts r
              JOIN Purchasing.PurchaseOrderDocumentOperations o ON o.TenantId=r.TenantId AND o.RequestId=r.RequestId
              JOIN Purchasing.DraftOrders p ON p.TenantId=o.TenantId AND p.Id=o.OrderId AND p.IsDeleted=0 AND p.State='Ordered'
              WHERE r.TenantId=@Tenant AND r.RequestId=@RequestId AND r.Operation='Dispose' AND o.OrderId=@OrderId;
            IF @Result IS NULL THROW 51004,'Disposal operation unavailable.',1;
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
