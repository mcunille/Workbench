// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static class PurchaseOrderDocumentSchema
{
    internal static void Protect(MigrationBuilder migrationBuilder, string migrationId)
    {
        foreach (var table in new[] { "PurchaseOrderDocuments", "PurchaseOrderDocumentOperations" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Purchasing].[{table}],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Purchasing].[{table}] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Purchasing].[{table}] AFTER UPDATE;
                GRANT SELECT ON [Purchasing].[{table}] TO [workbench_web];
                DENY INSERT,UPDATE,DELETE ON [Purchasing].[{table}] TO [workbench_web];
                """);
        migrationBuilder.Sql("""
            CREATE TRIGGER [Storage].[ReconcileFailedPurchaseOrderDocument] ON [Storage].[Revisions] AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                -- Failed is the existing definitive reconciliation outcome. Pending/ambiguous
                -- revisions retain their reservation and evidence until an operator resolves them.
                UPDATE o SET State=2 FROM Purchasing.PurchaseOrderDocumentOperations o
                JOIN inserted i ON i.TenantId=o.TenantId AND i.Id=o.RevisionId
                WHERE o.State=0 AND i.State=2;
            END;
            """);
        migrationBuilder.Sql(Prepare);
        migrationBuilder.Sql(Finish);
        migrationBuilder.Sql($"""
            GRANT EXECUTE ON [Purchasing].[PreparePurchaseOrderDocument] TO [workbench_web];
            GRANT EXECUTE ON [Purchasing].[FinishPurchaseOrderDocument] TO [workbench_web];
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260918060000_AddPurchaseOrderCommitment',N'{migrationId}');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
    private const string Prepare = """
        CREATE PROCEDURE [Purchasing].[PreparePurchaseOrderDocument]
          @OrderId uniqueidentifier,@RequestId uniqueidentifier,
          @ExpectedOrderVersion varbinary(max),@Kind int,
          @DocumentId uniqueidentifier=NULL,@ExpectedDocumentVersion varbinary(max)=NULL,
          @Label nvarchar(max)=NULL,@MediaType nvarchar(max)=NULL,@Extension nvarchar(max)=NULL,
          @Length bigint=NULL,@Sha256 nvarchar(max)=NULL,@ProviderAlias nvarchar(max)=NULL,@ActorUserId uniqueidentifier
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          IF @@TRANCOUNT=0 OR @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
             OR @Kind IS NULL OR @Kind NOT BETWEEN 0 AND 2
             OR @ExpectedOrderVersion IS NULL OR DATALENGTH(@ExpectedOrderVersion)<>8
             OR @ActorUserId IS NULL OR @ActorUserId='00000000-0000-0000-0000-000000000000'
            THROW 50076,'A request and current versions are required.',1;
          DECLARE @Whitespace nvarchar(64)=NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288);
          SET @Label=TRIM(@Whitespace FROM @Label);
          IF @Kind IN (0,1) AND (@Label IS NULL OR DATALENGTH(@Label) NOT BETWEEN 2 AND 400 OR LEN(LTRIM(RTRIM(@Label)))=0)
            THROW 50076,'A document label of 1 to 200 characters is required.',1;
          IF @Kind=0 AND (@DocumentId IS NOT NULL OR @ExpectedDocumentVersion IS NOT NULL OR @Length IS NULL OR @Length NOT BETWEEN 1 AND 10485760
            OR @Sha256 IS NULL OR DATALENGTH(@Sha256)<>128 OR @Sha256 COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9A-F]%'
            OR @ProviderAlias IS NULL OR DATALENGTH(@ProviderAlias) NOT BETWEEN 2 AND 128
            OR @MediaType IS NULL OR @Extension IS NULL
            OR NOT ((CONVERT(varbinary(max),@MediaType)=CONVERT(varbinary(max),N'application/pdf') AND CONVERT(varbinary(max),@Extension)=CONVERT(varbinary(max),N'pdf')) OR (CONVERT(varbinary(max),@MediaType)=CONVERT(varbinary(max),N'image/jpeg') AND CONVERT(varbinary(max),@Extension)=CONVERT(varbinary(max),N'jpg'))
              OR (CONVERT(varbinary(max),@MediaType)=CONVERT(varbinary(max),N'image/png') AND CONVERT(varbinary(max),@Extension)=CONVERT(varbinary(max),N'png')) OR (CONVERT(varbinary(max),@MediaType)=CONVERT(varbinary(max),N'image/webp') AND CONVERT(varbinary(max),@Extension)=CONVERT(varbinary(max),N'webp'))))
            THROW 50076,'Validated content identity is required.',1;
          IF @Kind IN (1,2) AND (@DocumentId IS NULL OR @ExpectedDocumentVersion IS NULL OR DATALENGTH(@ExpectedDocumentVersion)<>8
            OR @MediaType IS NOT NULL OR @Extension IS NOT NULL OR @Length IS NOT NULL OR @Sha256 IS NOT NULL)
            THROW 50076,'The document and current version are required.',1;
          IF @Kind=2 AND @Label IS NOT NULL THROW 50076,'Removal does not change the label.',1;
          DECLARE @TenantId uniqueidentifier,@OrderVersion binary(8),@OrderState nvarchar(16),@Deleted bit;
          SELECT @TenantId=TenantId,@OrderVersion=RowVersion,@OrderState=State,@Deleted=IsDeleted FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE Id=@OrderId;
          IF @TenantId IS NULL OR @Deleted=1 THROW 50078,'Purchase order not found.',1;
          IF @OrderState<>'Ordered' THROW 50077,'Record the purchase as ordered before adding invoice files.',1;
          IF EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocumentOperations WHERE TenantId=@TenantId AND RequestId=@RequestId)
          BEGIN
            IF NOT EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocumentOperations WHERE TenantId=@TenantId AND RequestId=@RequestId
              AND OrderId=@OrderId AND Kind=@Kind
              AND ExpectedOrderVersion=@ExpectedOrderVersion
              AND (@Kind=0 OR (DocumentId=@DocumentId AND ExpectedDocumentVersion=@ExpectedDocumentVersion))
              AND ((Label IS NULL AND @Label IS NULL) OR CONVERT(varbinary(max),Label)=CONVERT(varbinary(max),@Label))
              AND ((MediaType IS NULL AND @MediaType IS NULL) OR CONVERT(varbinary(max),MediaType)=CONVERT(varbinary(max),@MediaType))
              AND ((Extension IS NULL AND @Extension IS NULL) OR CONVERT(varbinary(max),Extension)=CONVERT(varbinary(max),@Extension))
              AND ((Length IS NULL AND @Length IS NULL) OR Length=@Length)
              AND ((Sha256 IS NULL AND @Sha256 IS NULL) OR CONVERT(varbinary(max),Sha256)=CONVERT(varbinary(max),@Sha256)))
              THROW 50077,'This request identifier was used for another document operation.',1;
            RETURN;
          END;
          IF @OrderVersion<>@ExpectedOrderVersion
            THROW 50077,'The purchase order changed. Reload before trying again.',1;
          IF @Kind IN (1,2) AND NOT EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND OrderId=@OrderId AND Id=@DocumentId AND RemovedAtUtc IS NULL)
            THROW 50078,'Document not found.',1;
          IF @Kind IN (1,2) AND NOT EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND Id=@DocumentId AND RowVersion=@ExpectedDocumentVersion)
            THROW 50077,'The document changed. Reload before trying again.',1;
          IF @Kind=0 AND (SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND OrderId=@OrderId AND RemovedAtUtc IS NULL)
            +(SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocumentOperations WHERE TenantId=@TenantId AND OrderId=@OrderId AND Kind=0 AND State=0)>=20
            THROW 50079,'This purchase order already has 20 current or pending documents. Remove a document before uploading another.',1;
          DECLARE @Id uniqueidentifier=NEWID(),@AttachmentId uniqueidentifier=NULL,@RevisionId uniqueidentifier=NULL,@Now datetimeoffset=SYSUTCDATETIME();
          IF @Kind=0
          BEGIN
            SET @DocumentId=NEWID(); SET @AttachmentId=NEWID(); SET @RevisionId=NEWID();
            INSERT Storage.Attachments(Id,TenantId,CreatedAtUtc,Held) VALUES(@AttachmentId,@TenantId,@Now,0);
            INSERT Storage.Revisions(Id,TenantId,AttachmentId,OperationId,ActorUserId,ProviderAlias,Source,MediaType,State,CreatedAtUtc)
              VALUES(@RevisionId,@TenantId,@AttachmentId,@Id,@ActorUserId,@ProviderAlias,'PurchaseOrderDocumentOriginal',@MediaType,0,@Now);
          END;
          INSERT Purchasing.PurchaseOrderDocumentOperations(Id,TenantId,OrderId,RequestId,DocumentId,AttachmentId,RevisionId,ActorUserId,
            Kind,State,Label,MediaType,Extension,Length,Sha256,ExpectedOrderVersion,ExpectedDocumentVersion,CreatedAtUtc)
          VALUES(@Id,@TenantId,@OrderId,@RequestId,@DocumentId,@AttachmentId,@RevisionId,@ActorUserId,
            @Kind,0,@Label,@MediaType,@Extension,@Length,@Sha256,@ExpectedOrderVersion,@ExpectedDocumentVersion,@Now);
        END;
        """;
    private const string Finish = """
        CREATE PROCEDURE [Purchasing].[FinishPurchaseOrderDocument] @RequestId uniqueidentifier,@Published bit
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          IF @@TRANCOUNT=0 THROW 50076,'Document finalization requires a transaction.',1;
          DECLARE @TenantId uniqueidentifier,@OrderId uniqueidentifier,@DocumentId uniqueidentifier,
            @AttachmentId uniqueidentifier,@RevisionId uniqueidentifier,@ActorUserId uniqueidentifier,@Kind int,@State int,
            @Label nvarchar(200),@MediaType nvarchar(100),@Extension nvarchar(8),@Length bigint,@Sha256 nvarchar(64),
            @ExpectedOrderVersion binary(8),@ExpectedDocumentVersion binary(8),
            @OrderVersion binary(8),@OrderState nvarchar(16),@Deleted bit,@Conflict bit=0,@Now datetimeoffset=SYSUTCDATETIME();
          SELECT @OrderId=OrderId FROM Purchasing.PurchaseOrderDocumentOperations WHERE RequestId=@RequestId;
          IF @OrderId IS NULL THROW 50078,'Operation not found.',1;
          SELECT @OrderVersion=RowVersion,@OrderState=State,@Deleted=IsDeleted FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE Id=@OrderId;
          SELECT @TenantId=TenantId,@DocumentId=DocumentId,@AttachmentId=AttachmentId,@RevisionId=RevisionId,@ActorUserId=ActorUserId,
            @Kind=Kind,@State=State,@Label=Label,@MediaType=MediaType,@Extension=Extension,@Length=Length,@Sha256=Sha256,
            @ExpectedOrderVersion=ExpectedOrderVersion,@ExpectedDocumentVersion=ExpectedDocumentVersion
          FROM Purchasing.PurchaseOrderDocumentOperations WITH(UPDLOCK,HOLDLOCK) WHERE RequestId=@RequestId;
          IF @State<>0 RETURN;
          IF @Kind=0 AND (@Published IS NULL OR @Published<>1) THROW 50076,'Immutable bytes must be published first.',1;
          IF @OrderVersion IS NULL OR @OrderVersion<>@ExpectedOrderVersion OR @OrderState<>'Ordered' OR @Deleted=1
            SET @Conflict=1;
          IF @Kind IN (1,2) AND NOT EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND OrderId=@OrderId AND Id=@DocumentId AND RowVersion=@ExpectedDocumentVersion AND RemovedAtUtc IS NULL)
            SET @Conflict=1;
          IF @Kind=0
          BEGIN
            UPDATE Storage.Revisions SET State=1,Length=@Length,Sha256=@Sha256 WHERE TenantId=@TenantId AND Id=@RevisionId AND State=0;
            IF @@ROWCOUNT<>1 THROW 50077,'The pending document revision is unavailable.',1;
            UPDATE Storage.Attachments SET CurrentRevisionId=@RevisionId WHERE TenantId=@TenantId AND Id=@AttachmentId AND DeletedAtUtc IS NULL;
            IF @@ROWCOUNT<>1 THROW 50077,'The pending document attachment is unavailable.',1;
          END;
          IF @Conflict=0
          BEGIN
            IF @Kind=0 INSERT Purchasing.PurchaseOrderDocuments(Id,TenantId,OrderId,AttachmentId,RevisionId,Label,MediaType,Extension,Length,Sha256,CreatedAtUtc)
              VALUES(@DocumentId,@TenantId,@OrderId,@AttachmentId,@RevisionId,@Label,@MediaType,@Extension,@Length,@Sha256,@Now);
            IF @Kind=1 UPDATE Purchasing.PurchaseOrderDocuments SET Label=@Label WHERE TenantId=@TenantId AND Id=@DocumentId;
            IF @Kind=2
            BEGIN
              SELECT @AttachmentId=AttachmentId FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND Id=@DocumentId;
              UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=@Now WHERE TenantId=@TenantId AND Id=@DocumentId;
            END;
            UPDATE Purchasing.DraftOrders SET UpdatedAtUtc=@Now,UpdatedByUserId=@ActorUserId WHERE TenantId=@TenantId AND Id=@OrderId;
            SELECT @OrderVersion=RowVersion FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@OrderId;
            UPDATE Purchasing.PurchaseOrderDocumentOperations SET State=1,ResultOrderVersion=@OrderVersion WHERE TenantId=@TenantId AND RequestId=@RequestId;
            INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
              VALUES(NEWID(),@TenantId,@ActorUserId,CASE @Kind WHEN 0 THEN 'purchasing.document.added' WHEN 1 THEN 'purchasing.document.renamed' ELSE 'purchasing.document.removed' END,'PurchaseOrder',@OrderId,@Now);
          END
          ELSE UPDATE Purchasing.PurchaseOrderDocumentOperations SET State=2 WHERE TenantId=@TenantId AND RequestId=@RequestId;
          IF (@Kind=0 AND @Conflict=1) OR (@Kind=2 AND @Conflict=0)
          BEGIN
            UPDATE Storage.Attachments SET DeletedAtUtc=@Now,DeleteAfterUtc=DATEADD(day,7,@Now) WHERE TenantId=@TenantId AND Id=@AttachmentId AND DeletedAtUtc IS NULL;
            IF @@ROWCOUNT=1 INSERT Operations.WorkItems(Id,TenantId,Kind,AttachmentId,State,Attempts,Generation,CreatedAtUtc,AvailableAtUtc)
              VALUES(NEWID(),@TenantId,1,@AttachmentId,0,0,0,@Now,DATEADD(day,7,@Now));
          END;
        END;
        """;
}
