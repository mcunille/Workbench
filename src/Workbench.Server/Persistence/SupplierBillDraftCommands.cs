// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierBillDraftCommands
{
    internal const string Sql = """
        CREATE PROCEDURE Purchasing.SaveSupplierBill @ActorId uniqueidentifier,@SessionId uniqueidentifier,
          @RequestId uniqueidentifier,@Command nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@LockResult int,
              @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),SESSION_CONTEXT(N'TenantId'));
            IF @TenantId IS NULL THROW 51003,'Current bill authority is required.',1;
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
            BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierBillsManage'; END TRY
            BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current bill authority is required.',1; THROW; END CATCH;
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid request identity.',1;
            DECLARE @Canonical nvarchar(max); EXEC Purchasing.CanonicalizeSupplierBill @Command,@Canonical OUTPUT;
            DECLARE @Operation varchar(16)=JSON_VALUE(@Canonical,'$.operation'),@BillId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.billId')),
              @PoId uniqueidentifier,@SupplierId uniqueidentifier,@Currency varchar(3),@State varchar(16),@RevisionId uniqueidentifier,
              @Version binary(8),@PoRevision int,@SupplierName nvarchar(200),@Now datetimeoffset=SYSUTCDATETIME(),@Result nvarchar(max);
            IF @Operation NOT IN('Create','Revise','Abandon') THROW 51000,'Invalid draft operation.',1;
            IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('revisionId','rationale','resolutions','expectedConfigurationVersion'))
              OR (@Operation<>'Create' AND EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('purchaseOrderId','supplierId','currency')))
              OR (@Operation='Create' AND JSON_VALUE(@Canonical,'$.billVersion') IS NOT NULL)
              OR (@Operation='Abandon' AND (JSON_QUERY(@Canonical,'$.revision') IS NOT NULL OR JSON_VALUE(@Canonical,'$.reason') IS NULL))
              OR (@Operation<>'Abandon' AND (JSON_QUERY(@Canonical,'$.revision') IS NULL OR JSON_VALUE(@Canonical,'$.reason') IS NOT NULL))
              THROW 51000,'Invalid draft operation fields.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.SupplierBillReceipts WHERE TenantId=@TenantId AND Operation=@Operation AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillReceipts WHERE TenantId=@TenantId AND Operation=@Operation AND RequestId=@RequestId
                AND ActorId=@ActorId AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
                THROW 51009,'Request identity has different content.',1;
              SELECT @Result=ResultJson FROM Purchasing.SupplierBillReceipts WHERE TenantId=@TenantId AND Operation=@Operation AND RequestId=@RequestId;
              COMMIT; SELECT @Result ResultJson; RETURN;
            END;
            IF @Operation='Create'
            BEGIN
              SELECT @PoId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
                @SupplierId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),@Currency=JSON_VALUE(@Canonical,'$.currency');
              IF @PoId IS NULL OR @SupplierId IS NULL OR @Currency IS NULL THROW 51000,'Bill identity is incomplete.',1;
              IF EXISTS(SELECT 1 FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId) THROW 51009,'Bill already exists.',1;
            END
            ELSE BEGIN
              SELECT @PoId=PurchaseOrderId,@SupplierId=SupplierId,@Currency=Currency FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId;
              IF @PoId IS NULL THROW 51004,'Bill unavailable.',1;
              IF JSON_VALUE(@Canonical,'$.billVersion') IS NULL THROW 51000,'Expected bill version is required.',1;
            END;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@PoId AND IsDeleted=0)
              THROW 51004,'Purchase unavailable.',1;
            SELECT @PoRevision=Revision FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK)
              WHERE TenantId=@TenantId AND Id=@PoId AND State='Ordered' AND IsDeleted=0 AND SupplierId=@SupplierId AND Currency=@Currency
                AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1);
            IF @PoRevision IS NULL THROW 51009,'Purchase identity or revision changed.',1;
            SELECT @SupplierName=Name FROM Purchasing.Suppliers WHERE TenantId=@TenantId AND Id=@SupplierId;
            IF @Operation='Create'
              INSERT Purchasing.SupplierBills(TenantId,Id,PurchaseOrderId,SupplierId,Currency,State) VALUES(@TenantId,@BillId,@PoId,@SupplierId,@Currency,'Draft');
            ELSE BEGIN
              SELECT @State=State,@RevisionId=CurrentRevisionId FROM Purchasing.SupplierBills WITH(UPDLOCK,HOLDLOCK)
                WHERE TenantId=@TenantId AND Id=@BillId AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.billVersion'),1);
              IF @State IS NULL OR @State NOT IN('Draft','Reviewed') THROW 51009,'Bill state or revision changed.',1;
            END;
            IF @Operation<>'Abandon'
            BEGIN
              DECLARE @DraftEvidence nvarchar(max),@Payload nvarchar(max)=JSON_QUERY(@Canonical,'$.revision');
              EXEC Purchasing.ValidateBillEvidence @TenantId,@PoId,@Payload,@DraftEvidence OUTPUT;
              SET @RevisionId=NEWID();
              INSERT Purchasing.SupplierBillRevisions(TenantId,Id,BillId,Sequence,PurchaseOrderRevision,SupplierName,Payload,NormalizedReference,ActorId,RecordedAtUtc)
                SELECT @TenantId,@RevisionId,@BillId,COALESCE(MAX(Sequence),0)+1,@PoRevision,@SupplierName,JSON_QUERY(@Canonical,'$.revision'),
                  Purchasing.NormalizeBillReference(JSON_VALUE(@Canonical,'$.revision.reference')),@ActorId,@Now
                FROM Purchasing.SupplierBillRevisions WHERE TenantId=@TenantId AND BillId=@BillId;
            END;
            UPDATE Purchasing.SupplierBills SET CurrentRevisionId=@RevisionId,CurrentReviewId=NULL,State=CASE WHEN @Operation='Abandon' THEN 'Abandoned' ELSE 'Draft' END
              WHERE TenantId=@TenantId AND Id=@BillId;
            SELECT @Result=(SELECT Id billId,CurrentRevisionId revisionId,CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) version,State state,@Now recordedAtUtc,
                JSON_QUERY('[]') recognitionEventIds,JSON_QUERY('[]') journalIds,
                JSON_QUERY((SELECT b.Id billId,b.CurrentRevisionId revisionId FROM Purchasing.SupplierBills b
                  JOIN Purchasing.SupplierBillRevisions r ON r.TenantId=b.TenantId AND r.Id=b.CurrentRevisionId
                  JOIN Purchasing.SupplierBillRevisions currentRevision ON currentRevision.TenantId=@TenantId AND currentRevision.Id=@RevisionId
                  WHERE b.TenantId=@TenantId AND b.SupplierId=@SupplierId AND b.Id<>@BillId AND b.State<>'Abandoned'
                    AND r.NormalizedReference=currentRevision.NormalizedReference ORDER BY b.Id FOR JSON PATH)) duplicates
              FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT Purchasing.SupplierBillReceipts(TenantId,Operation,RequestId,BillId,ActorId,CanonicalInput,ResultJson,RecordedAtUtc)
              VALUES(@TenantId,@Operation,@RequestId,@BillId,@ActorId,@Canonical,@Result,@Now);
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
