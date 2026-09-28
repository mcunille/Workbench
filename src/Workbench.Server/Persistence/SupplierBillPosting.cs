// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierBillPosting
{
    internal const string TablesSql = """
        CREATE TABLE Purchasing.SupplierBillPostings(
          TenantId uniqueidentifier NOT NULL,BillId uniqueidentifier NOT NULL,RevisionId uniqueidentifier NOT NULL,ReviewId uniqueidentifier NOT NULL,
          RecognitionRequestId uniqueidentifier NOT NULL,ResultJson nvarchar(max) NOT NULL,RecordedAtUtc datetimeoffset NOT NULL,
          CONSTRAINT PK_SupplierBillPostings PRIMARY KEY(TenantId,BillId),
          CONSTRAINT FK_SupplierBillPostings_Revision FOREIGN KEY(TenantId,BillId,RevisionId) REFERENCES Purchasing.SupplierBillRevisions(TenantId,BillId,Id),
          CONSTRAINT FK_SupplierBillPostings_Review FOREIGN KEY(TenantId,ReviewId) REFERENCES Purchasing.SupplierBillReviews(TenantId,Id),
          CONSTRAINT FK_SupplierBillPostings_Receipt FOREIGN KEY(TenantId,RecognitionRequestId) REFERENCES Purchasing.RecognitionGroupReceipts(TenantId,RequestId),
          CONSTRAINT CK_SupplierBillPostings_Json CHECK(ISJSON(ResultJson,OBJECT)=1));
        CREATE TABLE Purchasing.SupplierBillPostingEvents(
          TenantId uniqueidentifier NOT NULL,BillId uniqueidentifier NOT NULL,EventId uniqueidentifier NOT NULL,
          CONSTRAINT PK_SupplierBillPostingEvents PRIMARY KEY(TenantId,EventId),
          CONSTRAINT FK_SupplierBillPostingEvents_Posting FOREIGN KEY(TenantId,BillId) REFERENCES Purchasing.SupplierBillPostings(TenantId,BillId),
          CONSTRAINT FK_SupplierBillPostingEvents_Event FOREIGN KEY(TenantId,EventId) REFERENCES Purchasing.RecognitionSideEvents(TenantId,Id));
        """;

    // A source adapter needs the output parameter without forwarding the nested kernel result set.
    // Existing callers retain their default result-set behavior, including exact retries.
    internal const string KernelOutputSql = """
        DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.PostRecognition'));
        IF @Definition IS NULL OR CHARINDEX(N'@ResultJson nvarchar(max)=NULL OUTPUT',@Definition)=0
          OR CHARINDEX(N'SELECT ResultJson FROM Purchasing.RecognitionGroupReceipts',@Definition)=0
          THROW 50020,'Unsupported bill recognition adapter predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,N'@ResultJson nvarchar(max)=NULL OUTPUT',N'@ResultJson nvarchar(max)=NULL OUTPUT,@SuppressResult bit=0');
        SET @Definition=REPLACE(@Definition,N'SELECT ResultJson FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;',
          N'SELECT @ResultJson=ResultJson FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId; IF @SuppressResult=0 SELECT @ResultJson ResultJson;');
        SET @Definition=REPLACE(@Definition,N'SELECT @Result ResultJson;',N'IF @SuppressResult=0 SELECT @Result ResultJson;');
        EXEC sys.sp_executesql @Definition;
        """;

    internal const string Sql = """
        CREATE PROCEDURE Purchasing.PostSupplierBill @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@LockResult int,
              @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),SESSION_CONTEXT(N'TenantId'));
            IF @TenantId IS NULL THROW 51003,'Current bill authority is required.',1;
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
            BEGIN TRY
              EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierBillsManage';
              EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierBillsPost';
            END TRY BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current bill authority is required.',1; THROW; END CATCH;
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid request identity.',1;
            DECLARE @Canonical nvarchar(max); EXEC Purchasing.CanonicalizeSupplierBill @Command,@Canonical OUTPUT;
            IF JSON_VALUE(@Canonical,'$.operation')<>'Post' OR JSON_VALUE(@Canonical,'$.billVersion') IS NULL
              OR JSON_VALUE(@Canonical,'$.revisionId') IS NULL OR JSON_VALUE(@Canonical,'$.expectedConfigurationVersion') IS NULL
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('revision','reason','purchaseOrderId','supplierId','currency','rationale','resolutions'))
              THROW 51000,'Invalid posting fields.',1;
            DECLARE @BillId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.billId')),
              @RevisionId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.revisionId')),@Result nvarchar(max);
            IF EXISTS(SELECT 1 FROM Purchasing.SupplierBillReceipts WHERE TenantId=@TenantId AND Operation='Post' AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillReceipts WHERE TenantId=@TenantId AND Operation='Post' AND RequestId=@RequestId AND ActorId=@ActorId
                AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical)) THROW 51009,'Request identity has different content.',1;
              SELECT @Result=ResultJson FROM Purchasing.SupplierBillReceipts WHERE TenantId=@TenantId AND Operation='Post' AND RequestId=@RequestId;
              COMMIT; SELECT @Result ResultJson; RETURN;
            END;
            DECLARE @PoId uniqueidentifier,@SupplierId uniqueidentifier,@Currency varchar(3),@ReviewId uniqueidentifier,@Payload nvarchar(max),
              @PoVersion binary(8)=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1),@Resolutions nvarchar(max),@Evidence nvarchar(max);
            SELECT @PoId=PurchaseOrderId,@SupplierId=SupplierId,@Currency=Currency FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId;
            IF @PoId IS NULL THROW 51004,'Bill unavailable.',1;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@PoId AND RowVersion=@PoVersion
              AND State='Ordered' AND IsDeleted=0 AND SupplierId=@SupplierId AND Currency=@Currency) THROW 51009,'Purchase revision changed.',1;
            SELECT @ReviewId=CurrentReviewId FROM Purchasing.SupplierBills WITH(UPDLOCK,HOLDLOCK)
              WHERE TenantId=@TenantId AND Id=@BillId AND State='Reviewed' AND CurrentRevisionId=@RevisionId
                AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.billVersion'),1);
            IF @ReviewId IS NULL THROW 51009,'Reviewed bill revision changed.',1;
            SELECT @Resolutions=Resolutions FROM Purchasing.SupplierBillReviews WHERE TenantId=@TenantId AND Id=@ReviewId AND RevisionId=@RevisionId AND PurchaseOrderVersion=@PoVersion;
            IF @Resolutions IS NULL THROW 51009,'Purchase must be reviewed again.',1;
            SELECT @Payload=Payload FROM Purchasing.SupplierBillRevisions WHERE TenantId=@TenantId AND Id=@RevisionId AND BillId=@BillId;
            IF JSON_VALUE(@Payload,'$.kind')<>'Invoice' THROW 51000,'Pro forma requests cannot create a payable.',1;
            EXEC Purchasing.ValidateReviewedBill @TenantId,@BillId,@RevisionId,@Resolutions,@Evidence OUTPUT;
            DECLARE @Units TABLE(Id uniqueidentifier PRIMARY KEY,Input nvarchar(max));
            DECLARE @Unit nvarchar(max),@UnitId uniqueidentifier,@Gross decimal(28,4),@UnitEvidence nvarchar(max),@Side nvarchar(max),@UnitJson nvarchar(max);
            DECLARE units_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT value FROM OPENJSON(@Payload,'$.units') ORDER BY JSON_VALUE(value,'$.unitId');
            OPEN units_cursor; FETCH NEXT FROM units_cursor INTO @Unit;
            WHILE @@FETCH_STATUS=0
            BEGIN
              SET @UnitId=CONVERT(uniqueidentifier,JSON_VALUE(@Unit,'$.unitId'));
              SELECT @Gross=CONVERT(decimal(28,4),SUM(CASE WHEN JSON_VALUE(value,'$.kind')='Discount' THEN -CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount'))
                ELSE CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount')) END)) FROM OPENJSON(@Unit,'$.components');
              SET @UnitEvidence=JSON_MODIFY(JSON_QUERY(@Unit,'$.evidence'),'$.schemaVersion',1);
              SET @UnitEvidence=JSON_MODIFY(@UnitEvidence,'$.sourceCapacityQuantity',JSON_VALUE(@Unit,'$.quantity'));
              SET @UnitEvidence=JSON_MODIFY(@UnitEvidence,'$.sourceCapacityAmount',CONVERT(nvarchar(60),@Gross));
              -- No inventory-state assertion is synthesized: variance that needs held-state proof fails closed in BK-04.
              IF JSON_VALUE(@Evidence,'$[0].revisionId') IS NOT NULL
              BEGIN
                SET @UnitEvidence=JSON_MODIFY(@UnitEvidence,'$.documentRevision',JSON_VALUE(@Evidence,'$[0].revisionId'));
                SET @UnitEvidence=JSON_MODIFY(@UnitEvidence,'$.documentDigest',JSON_VALUE(@Evidence,'$[0].digest'));
              END;
              SET @Side=(SELECT 'Invoice' side,1 eventRevision,
                @BillId sourceId,@RevisionId sourceRevision,JSON_VALUE(@Unit,'$.componentKey') sourceComponentKey,'whole' subdivisionKey,
                JSON_VALUE(@Unit,'$.quantity') sourceQuantity,CONVERT(nvarchar(60),@Gross) sourceAmount,
                JSON_VALUE(@Payload,'$.documentDate') documentDate,JSON_VALUE(@Payload,'$.effectiveDate') effectiveDate,
                JSON_QUERY(@UnitEvidence) evidence,JSON_QUERY(@Unit,'$.components') components FOR JSON PATH);
              SET @UnitJson=(SELECT @UnitId unitId,JSON_VALUE(@Unit,'$.classification') classification,JSON_VALUE(@Unit,'$.goodsReference') goodsReference,
                JSON_VALUE(@Unit,'$.quantity') quantity,JSON_VALUE(@Unit,'$.quantityUnit') quantityUnit,
                CONVERT(int,JSON_VALUE(@Unit,'$.expectedPriorEventRevision')) expectedPriorEventRevision,JSON_QUERY(@Side) sides FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              INSERT @Units VALUES(@UnitId,@UnitJson);
              FETCH NEXT FROM units_cursor INTO @Unit;
            END;
            CLOSE units_cursor; DEALLOCATE units_cursor;
            DECLARE @UnitsJson nvarchar(max)=(SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),Input),N',') WITHIN GROUP(ORDER BY Id)+N']' FROM @Units),
              @RecognitionCommand nvarchar(max),@KernelRequest uniqueidentifier=NEWID(),@KernelResult nvarchar(max),@Now datetimeoffset;
            SET @RecognitionCommand=(SELECT 1 schemaVersion,'Post' operation,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion') expectedConfigurationVersion,
              @PoId purchaseOrderId,JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion') expectedPurchaseOrderVersion,@SupplierId supplierId,@Currency currency,
              JSON_VALUE(@Payload,'$.postingDate') postingDate,JSON_QUERY(@UnitsJson) units FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            IF DATALENGTH(@RecognitionCommand)>262144 THROW 51000,'Derived recognition command exceeds its bound.',1;
            EXEC Purchasing.PostRecognition @ActorId=@ActorId,@SessionId=@SessionId,@RequestId=@KernelRequest,
              @RequiredPermission=N'SupplierBillsPost',@Command=@RecognitionCommand,@ResultJson=@KernelResult OUTPUT,@SuppressResult=1;
            SET @Now=CONVERT(datetimeoffset,JSON_VALUE(@KernelResult,'$.recordedAtUtc'));
            UPDATE Purchasing.SupplierBills SET State='Posted' WHERE TenantId=@TenantId AND Id=@BillId;
            SELECT @Result=(SELECT Id billId,CurrentRevisionId revisionId,CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) version,State state,@ReviewId reviewId,@Now recordedAtUtc,
              JSON_QUERY(@KernelResult,'$.eventIds') recognitionEventIds,JSON_QUERY(@KernelResult,'$.journalIds') journalIds
              FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT Purchasing.SupplierBillPostings(TenantId,BillId,RevisionId,ReviewId,RecognitionRequestId,ResultJson,RecordedAtUtc)
              VALUES(@TenantId,@BillId,@RevisionId,@ReviewId,@KernelRequest,@Result,@Now);
            INSERT Purchasing.SupplierBillPostingEvents(TenantId,BillId,EventId)
              SELECT @TenantId,@BillId,CONVERT(uniqueidentifier,value) FROM OPENJSON(@KernelResult,'$.eventIds');
            INSERT Purchasing.SupplierBillReceipts(TenantId,Operation,RequestId,BillId,ActorId,CanonicalInput,ResultJson,RecordedAtUtc)
              VALUES(@TenantId,'Post',@RequestId,@BillId,@ActorId,@Canonical,@Result,@Now);
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
