// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierBillReviewCommands
{
    internal const string TablesSql = """
        CREATE TABLE Purchasing.SupplierBillReviews(
          TenantId uniqueidentifier NOT NULL,Id uniqueidentifier NOT NULL,BillId uniqueidentifier NOT NULL,RevisionId uniqueidentifier NOT NULL,
          PurchaseOrderRevision int NOT NULL,PurchaseOrderVersion binary(8) NOT NULL,ActorId uniqueidentifier NOT NULL,
          Rationale nvarchar(2000) NOT NULL,Resolutions nvarchar(max) NOT NULL,EvidenceJson nvarchar(max) NOT NULL,RecordedAtUtc datetimeoffset NOT NULL,
          CONSTRAINT PK_SupplierBillReviews PRIMARY KEY(TenantId,Id),
          CONSTRAINT FK_SupplierBillReviews_Revision FOREIGN KEY(TenantId,BillId,RevisionId) REFERENCES Purchasing.SupplierBillRevisions(TenantId,BillId,Id),
          CONSTRAINT FK_SupplierBillReviews_Actor FOREIGN KEY(TenantId,ActorId) REFERENCES [Identity].Users(TenantId,Id),
          CONSTRAINT CK_SupplierBillReviews_Json CHECK(ISJSON(Resolutions,ARRAY)=1 AND ISJSON(EvidenceJson,ARRAY)=1));
        CREATE TABLE Purchasing.SupplierBillEvidence(
          TenantId uniqueidentifier NOT NULL,ReviewId uniqueidentifier NOT NULL,DocumentId uniqueidentifier NOT NULL,RevisionId uniqueidentifier NOT NULL,
          CONSTRAINT PK_SupplierBillEvidence PRIMARY KEY(TenantId,ReviewId,DocumentId),
          CONSTRAINT FK_SupplierBillEvidence_Review FOREIGN KEY(TenantId,ReviewId) REFERENCES Purchasing.SupplierBillReviews(TenantId,Id),
          CONSTRAINT FK_SupplierBillEvidence_Document FOREIGN KEY(TenantId,DocumentId) REFERENCES Purchasing.PurchaseOrderDocuments(TenantId,Id),
          CONSTRAINT FK_SupplierBillEvidence_Revision FOREIGN KEY(TenantId,RevisionId) REFERENCES Storage.Revisions(TenantId,Id));
        ALTER TABLE Purchasing.SupplierBills ADD CurrentReviewId uniqueidentifier NULL,
          CONSTRAINT FK_SupplierBills_Review FOREIGN KEY(TenantId,CurrentReviewId) REFERENCES Purchasing.SupplierBillReviews(TenantId,Id);
        """;

    internal const string EvidenceSql = """
        CREATE PROCEDURE Purchasing.ValidateBillEvidence @TenantId uniqueidentifier,@PurchaseOrderId uniqueidentifier,
          @Payload nvarchar(max),@Evidence nvarchar(max) OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 THROW 51000,'Source transaction required.',1;
          IF EXISTS(SELECT JSON_VALUE(value,'$.documentId') FROM OPENJSON(@Payload,'$.documents') GROUP BY JSON_VALUE(value,'$.documentId') HAVING COUNT(*)>1)
            THROW 51000,'Duplicate bill document.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.documents') j
            LEFT JOIN Purchasing.PurchaseOrderDocuments d WITH(UPDLOCK,HOLDLOCK) ON d.TenantId=@TenantId AND d.OrderId=@PurchaseOrderId
              AND d.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId')) AND d.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId'))
            LEFT JOIN Storage.Revisions r WITH(UPDLOCK,HOLDLOCK) ON r.TenantId=d.TenantId AND r.Id=d.RevisionId
            LEFT JOIN Storage.Attachments a WITH(UPDLOCK,HOLDLOCK) ON a.TenantId=d.TenantId AND a.Id=d.AttachmentId
            WHERE d.Id IS NULL OR d.RemovedAtUtc IS NOT NULL OR r.Id IS NULL OR r.State<>1 OR a.Id IS NULL OR a.DeletedAtUtc IS NOT NULL
              OR EXISTS(SELECT 1 FROM Storage.RecoveryFiles f WHERE f.TenantId=@TenantId AND f.RevisionId=d.RevisionId))
            THROW 51004,'Bill document unavailable.',1;
          SET @Evidence=(SELECT d.Id documentId,d.RevisionId revisionId,d.Sha256 digest,d.Length length,d.Label label
            FROM OPENJSON(@Payload,'$.documents') j JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@TenantId
              AND d.Id=CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId')) ORDER BY d.Id FOR JSON PATH);
          SET @Evidence=COALESCE(@Evidence,N'[]');
        END;
        """;

    internal const string ValidateSql = """
        CREATE PROCEDURE Purchasing.ValidateReviewedBill @TenantId uniqueidentifier,@BillId uniqueidentifier,@RevisionId uniqueidentifier,
          @Resolutions nvarchar(max),@Evidence nvarchar(max) OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          DECLARE @Payload nvarchar(max),@SupplierId uniqueidentifier,@PoId uniqueidentifier,@Reference nvarchar(200),@Kind varchar(16);
          SELECT @Payload=r.Payload,@SupplierId=b.SupplierId,@PoId=b.PurchaseOrderId,@Reference=r.NormalizedReference
            FROM Purchasing.SupplierBills b JOIN Purchasing.SupplierBillRevisions r ON r.TenantId=b.TenantId AND r.Id=b.CurrentRevisionId
            WHERE b.TenantId=@TenantId AND b.Id=@BillId AND r.Id=@RevisionId;
          IF @Payload IS NULL THROW 51009,'Bill revision changed.',1;
          SET @Kind=JSON_VALUE(@Payload,'$.kind');
          IF @Reference IS NULL OR JSON_VALUE(@Payload,'$.documentDate') IS NULL OR JSON_VALUE(@Payload,'$.effectiveDate') IS NULL
            OR JSON_VALUE(@Payload,'$.postingDate') IS NULL OR JSON_VALUE(@Payload,'$.total') IS NULL
            OR (JSON_VALUE(@Payload,'$.dueDate') IS NULL AND JSON_VALUE(@Payload,'$.terms') IS NULL)
            OR JSON_VALUE(@Payload,'$.effectiveDate')>JSON_VALUE(@Payload,'$.postingDate')
            OR JSON_VALUE(@Payload,'$.dueDate')<JSON_VALUE(@Payload,'$.documentDate')
            OR (NOT EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.documents')) AND JSON_VALUE(@Payload,'$.missingEvidenceReason') IS NULL)
            THROW 51000,'Bill review requires complete dates, terms, total and evidence.',1;
          IF (SELECT COUNT(*) FROM OPENJSON(@Payload,'$.units')) NOT BETWEEN 1 AND 1000
            OR EXISTS(SELECT JSON_VALUE(value,'$.unitId') FROM OPENJSON(@Payload,'$.units') GROUP BY JSON_VALUE(value,'$.unitId') HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.units') GROUP BY JSON_VALUE(value,'$.componentKey') COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
            THROW 51000,'Bill units must have distinct bounded identities.',1;
          DECLARE @Units TABLE(Id uniqueidentifier,Input nvarchar(max),Gross decimal(38,4),Cost decimal(38,4),Tax decimal(38,4));
          INSERT @Units SELECT CONVERT(uniqueidentifier,JSON_VALUE(u.value,'$.unitId')),u.value,
            SUM(CASE WHEN JSON_VALUE(c.value,'$.kind')='Discount' THEN -CONVERT(decimal(38,4),JSON_VALUE(c.value,'$.amount')) ELSE CONVERT(decimal(38,4),JSON_VALUE(c.value,'$.amount')) END),
            SUM(CASE WHEN JSON_VALUE(c.value,'$.kind')='RecoverableTax' THEN 0 WHEN JSON_VALUE(c.value,'$.kind')='Discount' THEN -CONVERT(decimal(38,4),JSON_VALUE(c.value,'$.amount')) ELSE CONVERT(decimal(38,4),JSON_VALUE(c.value,'$.amount')) END),
            SUM(CASE WHEN JSON_VALUE(c.value,'$.kind')='RecoverableTax' THEN CONVERT(decimal(38,4),JSON_VALUE(c.value,'$.amount')) ELSE 0 END)
            FROM OPENJSON(@Payload,'$.units') u OUTER APPLY OPENJSON(u.value,'$.components') c GROUP BY u.value;
          IF EXISTS(SELECT 1 FROM @Units WHERE Cost IS NULL OR Cost<0 OR Tax<0 OR Gross>999999999999999999999999.9999
              OR JSON_VALUE(Input,'$.classification') COLLATE Latin1_General_100_BIN2 NOT IN('Expense','Inventory')
              OR CONVERT(decimal(28,6),JSON_VALUE(Input,'$.quantity'))<=0
              OR TRY_CONVERT(int,JSON_VALUE(Input,'$.expectedPriorEventRevision')) IS NULL OR TRY_CONVERT(int,JSON_VALUE(Input,'$.expectedPriorEventRevision'))<0
              OR (SELECT COUNT(*) FROM OPENJSON(Input,'$.components')) NOT BETWEEN 1 AND 1000)
            OR (SELECT SUM(Gross) FROM @Units)<>CONVERT(decimal(28,4),JSON_VALUE(@Payload,'$.total'))
            THROW 51000,'Bill components must equal its exact total.',1;
          IF EXISTS(SELECT 1 FROM @Units u CROSS APPLY OPENJSON(u.Input,'$.components') c
              WHERE JSON_VALUE(c.value,'$.kind') COLLATE Latin1_General_100_BIN2 NOT IN('BaseCost','Discount','Freight','Charge','NonrecoverableTax','RecoverableTax','Rounding'))
            OR EXISTS(SELECT u.Id,JSON_VALUE(c.value,'$.componentKey') COLLATE Latin1_General_100_BIN2 FROM @Units u CROSS APPLY OPENJSON(u.Input,'$.components') c
              GROUP BY u.Id,JSON_VALUE(c.value,'$.componentKey') COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
            THROW 51000,'Bill component identities or kinds are invalid.',1;
          IF EXISTS(SELECT 1 FROM @Units u CROSS APPLY OPENJSON(u.Input,'$.components') c
            WHERE JSON_VALUE(c.value,'$.kind') IN('Discount','Freight','Charge','NonrecoverableTax','Rounding') AND NOT EXISTS(
              SELECT 1 FROM OPENJSON(u.Input,'$.components') b WHERE JSON_VALUE(b.value,'$.componentKey') COLLATE Latin1_General_100_BIN2=JSON_VALUE(c.value,'$.assignedCostComponentKey')
                AND JSON_VALUE(b.value,'$.kind') IN('BaseCost','Freight','Charge')))
            THROW 51000,'Bill charges and adjustments require a named cost assignment.',1;
          IF @Kind='Invoice' AND EXISTS(SELECT 1 FROM @Units WHERE JSON_VALUE(Input,'$.evidence.invoiceEligible')<>'true'
              OR JSON_VALUE(Input,'$.evidence.presentObligation')<>'true'
              OR (JSON_VALUE(Input,'$.expectedPriorEventRevision')='0' AND JSON_VALUE(Input,'$.evidence.enforceableRight')<>'true')
              OR (Tax>0 AND (COALESCE(JSON_VALUE(Input,'$.evidence.taxEntitlement'),'false')<>'true' OR JSON_VALUE(Input,'$.evidence.taxPolicyReference') IS NULL)))
            THROW 51000,'Invoice eligibility or tax entitlement is unsupported.',1;
          DECLARE @Conflicts TABLE(BillId uniqueidentifier,RevisionId uniqueidentifier);
          INSERT @Conflicts SELECT b.Id,b.CurrentRevisionId FROM Purchasing.SupplierBills b
            JOIN Purchasing.SupplierBillRevisions r ON r.TenantId=b.TenantId AND r.Id=b.CurrentRevisionId
            WHERE b.TenantId=@TenantId AND b.SupplierId=@SupplierId AND b.Id<>@BillId AND b.State<>'Abandoned' AND r.NormalizedReference=@Reference;
          IF @Resolutions IS NULL OR ISJSON(@Resolutions,ARRAY)<>1 THROW 51000,'Explicit duplicate resolutions required.',1;
          DECLARE @Resolved TABLE(BillId uniqueidentifier,RevisionId uniqueidentifier);
          INSERT @Resolved SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.conflictingBillId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.conflictingRevisionId')) FROM OPENJSON(@Resolutions);
          IF EXISTS(SELECT BillId,RevisionId FROM @Conflicts EXCEPT SELECT BillId,RevisionId FROM @Resolved)
            OR EXISTS(SELECT BillId,RevisionId FROM @Resolved EXCEPT SELECT BillId,RevisionId FROM @Conflicts)
            OR EXISTS(SELECT BillId FROM @Resolved GROUP BY BillId HAVING COUNT(*)>1)
            THROW 51009,'Duplicate references require resolution of the current conflict set.',1;
          EXEC Purchasing.ValidateBillEvidence @TenantId,@PoId,@Payload,@Evidence OUTPUT;
        END;
        """;

    internal const string Sql = """
        CREATE PROCEDURE Purchasing.ReviewSupplierBill @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
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
            IF JSON_VALUE(@Canonical,'$.operation')<>'Review' OR JSON_VALUE(@Canonical,'$.billVersion') IS NULL
              OR JSON_VALUE(@Canonical,'$.revisionId') IS NULL OR JSON_VALUE(@Canonical,'$.rationale') IS NULL OR JSON_QUERY(@Canonical,'$.resolutions') IS NULL
              OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('revision','reason','purchaseOrderId','supplierId','currency','expectedConfigurationVersion'))
              THROW 51000,'Invalid review fields.',1;
            DECLARE @BillId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.billId')),
              @RevisionId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.revisionId')),@Result nvarchar(max);
            IF EXISTS(SELECT 1 FROM Purchasing.SupplierBillReceipts WHERE TenantId=@TenantId AND Operation='Review' AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillReceipts WHERE TenantId=@TenantId AND Operation='Review' AND RequestId=@RequestId AND ActorId=@ActorId
                AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical)) THROW 51009,'Request identity has different content.',1;
              SELECT @Result=ResultJson FROM Purchasing.SupplierBillReceipts WHERE TenantId=@TenantId AND Operation='Review' AND RequestId=@RequestId;
              COMMIT; SELECT @Result ResultJson; RETURN;
            END;
            DECLARE @PoId uniqueidentifier,@PoRevision int,@PoVersion binary(8)=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1);
            SELECT @PoId=PurchaseOrderId FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId;
            IF @PoId IS NULL THROW 51004,'Bill unavailable.',1;
            SELECT @PoRevision=p.Revision FROM Purchasing.DraftOrders p WITH(UPDLOCK,HOLDLOCK) JOIN Purchasing.SupplierBills b ON b.TenantId=p.TenantId AND b.PurchaseOrderId=p.Id
              WHERE b.TenantId=@TenantId AND b.Id=@BillId AND p.RowVersion=@PoVersion AND p.State='Ordered' AND p.IsDeleted=0 AND p.SupplierId=b.SupplierId AND p.Currency=b.Currency;
            IF @PoRevision IS NULL THROW 51009,'Purchase revision changed.',1;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBills WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@BillId AND CurrentRevisionId=@RevisionId
              AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.billVersion'),1) AND State IN('Draft','Reviewed')) THROW 51009,'Bill revision changed.',1;
            DECLARE @Evidence nvarchar(max),@Resolutions nvarchar(max)=JSON_QUERY(@Canonical,'$.resolutions'),@ReviewId uniqueidentifier=NEWID(),@Now datetimeoffset=SYSUTCDATETIME();
            EXEC Purchasing.ValidateReviewedBill @TenantId,@BillId,@RevisionId,@Resolutions,@Evidence OUTPUT;
            INSERT Purchasing.SupplierBillReviews(TenantId,Id,BillId,RevisionId,PurchaseOrderRevision,PurchaseOrderVersion,ActorId,Rationale,Resolutions,EvidenceJson,RecordedAtUtc)
              VALUES(@TenantId,@ReviewId,@BillId,@RevisionId,@PoRevision,@PoVersion,@ActorId,JSON_VALUE(@Canonical,'$.rationale'),@Resolutions,@Evidence,@Now);
            INSERT Purchasing.SupplierBillEvidence(TenantId,ReviewId,DocumentId,RevisionId)
              SELECT @TenantId,@ReviewId,CONVERT(uniqueidentifier,JSON_VALUE(value,'$.documentId')),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.revisionId')) FROM OPENJSON(@Evidence);
            UPDATE Purchasing.SupplierBills SET State='Reviewed',CurrentReviewId=@ReviewId WHERE TenantId=@TenantId AND Id=@BillId;
            SELECT @Result=(SELECT Id billId,CurrentRevisionId revisionId,CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) version,State state,@ReviewId reviewId,@Now recordedAtUtc,
              JSON_QUERY('[]') recognitionEventIds,JSON_QUERY('[]') journalIds FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT Purchasing.SupplierBillReceipts(TenantId,Operation,RequestId,BillId,ActorId,CanonicalInput,ResultJson,RecordedAtUtc)
              VALUES(@TenantId,'Review',@RequestId,@BillId,@ActorId,@Canonical,@Result,@Now);
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
