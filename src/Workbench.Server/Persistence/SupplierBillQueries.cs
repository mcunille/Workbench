// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierBillQueries
{
    internal const string AuthoritySql = """
        CREATE PROCEDURE Purchasing.RequireSupplierBillRead @ActorId uniqueidentifier,@SessionId uniqueidentifier
        AS BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 THROW 51000,'Read transaction required.',1;
          BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierBillsManage'; END TRY
          BEGIN CATCH
            IF ERROR_NUMBER()<>50903 THROW;
            BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'AccountingReportsRead'; END TRY
            BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current bill read authority is required.',1; THROW; END CATCH;
          END CATCH;
          DECLARE @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),SESSION_CONTEXT(N'TenantId')),@LockResult int;
          IF @Resource IS NULL THROW 51003,'Current bill read authority is required.',1;
          EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
          IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
        END;
        """;

    internal const string ListSql = """
        CREATE PROCEDURE Purchasing.ReadSupplierBills @ActorId uniqueidentifier,@SessionId uniqueidentifier,
          @PurchaseOrderId uniqueidentifier,@AfterId uniqueidentifier=NULL,@Take int=50
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION; EXEC Purchasing.RequireSupplierBillRead @ActorId,@SessionId;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Result nvarchar(max),@Next uniqueidentifier;
            IF @Take IS NULL OR @Take NOT BETWEEN 1 AND 100 THROW 51000,'Bill page size must be 1 to 100.',1;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@PurchaseOrderId) THROW 51004,'Purchase unavailable.',1;
            DECLARE @Page TABLE(Id uniqueidentifier PRIMARY KEY,State varchar(16),RevisionId uniqueidentifier,Version binary(8),Reference nvarchar(200));
            INSERT @Page SELECT TOP(@Take+1) b.Id,b.State,b.CurrentRevisionId,b.RowVersion,JSON_VALUE(r.Payload,'$.reference')
              FROM Purchasing.SupplierBills b JOIN Purchasing.SupplierBillRevisions r ON r.TenantId=b.TenantId AND r.Id=b.CurrentRevisionId
              WHERE b.TenantId=@TenantId AND b.PurchaseOrderId=@PurchaseOrderId AND (@AfterId IS NULL OR b.Id>@AfterId) ORDER BY b.Id;
            IF (SELECT COUNT(*) FROM @Page)>@Take SELECT TOP(1) @Next=Id FROM (SELECT TOP(@Take) Id FROM @Page ORDER BY Id) p ORDER BY Id DESC;
            SET @Result=(SELECT JSON_QUERY(COALESCE((SELECT TOP(@Take) Id billId,State state,RevisionId revisionId,CONVERT(varchar(18),Version,1) version,Reference reference
              FROM @Page ORDER BY Id FOR JSON PATH),N'[]')) items,@Next nextId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;

    internal const string DetailSql = """
        CREATE PROCEDURE Purchasing.ReadSupplierBill @ActorId uniqueidentifier,@SessionId uniqueidentifier,@BillId uniqueidentifier
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION; EXEC Purchasing.RequireSupplierBillRead @ActorId,@SessionId;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Result nvarchar(max),@Evidence nvarchar(max);
            IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId) THROW 51004,'Bill unavailable.',1;
            SELECT @Evidence=r.EvidenceJson FROM Purchasing.SupplierBills b LEFT JOIN Purchasing.SupplierBillReviews r ON r.TenantId=b.TenantId AND r.Id=b.CurrentReviewId
              WHERE b.TenantId=@TenantId AND b.Id=@BillId;
            SET @Result=(SELECT b.Id billId,b.PurchaseOrderId purchaseOrderId,b.SupplierId supplierId,b.Currency currency,b.State state,
              b.CurrentRevisionId revisionId,CONVERT(varchar(18),CONVERT(binary(8),b.RowVersion),1) version,r.SupplierName supplierName,JSON_QUERY(r.Payload) revision,
              JSON_QUERY((SELECT v.Id reviewId,v.ActorId actorId,v.Rationale rationale,v.RecordedAtUtc recordedAtUtc,JSON_QUERY(v.Resolutions) resolutions
                FROM Purchasing.SupplierBillReviews v WHERE v.TenantId=b.TenantId AND v.Id=b.CurrentReviewId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) review,
              JSON_QUERY(p.ResultJson) posting,
              JSON_QUERY(COALESCE((SELECT JSON_VALUE(j.value,'$.documentId') documentId,JSON_VALUE(j.value,'$.revisionId') revisionId,
                  JSON_VALUE(j.value,'$.digest') digest,CONVERT(bigint,JSON_VALUE(j.value,'$.length')) length,JSON_VALUE(j.value,'$.label') label,
                  CONVERT(bit,CASE WHEN d.Id IS NOT NULL AND d.RemovedAtUtc IS NULL AND s.State=1 AND a.Id IS NOT NULL AND a.DeletedAtUtc IS NULL
                    AND NOT EXISTS(SELECT 1 FROM Storage.RecoveryFiles f WHERE f.TenantId=@TenantId AND f.RevisionId=s.Id) THEN 1 ELSE 0 END) available
                FROM OPENJSON(@Evidence) j LEFT JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@TenantId AND d.Id=CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId'))
                LEFT JOIN Storage.Revisions s ON s.TenantId=@TenantId AND s.Id=CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId'))
                LEFT JOIN Storage.Attachments a ON a.TenantId=@TenantId AND a.Id=d.AttachmentId ORDER BY d.Id FOR JSON PATH),N'[]')) evidence
              FROM Purchasing.SupplierBills b JOIN Purchasing.SupplierBillRevisions r ON r.TenantId=b.TenantId AND r.Id=b.CurrentRevisionId
                LEFT JOIN Purchasing.SupplierBillPostings p ON p.TenantId=b.TenantId AND p.BillId=b.Id
              WHERE b.TenantId=@TenantId AND b.Id=@BillId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;

    internal const string HistorySql = """
        CREATE PROCEDURE Purchasing.ReadSupplierBillHistory @ActorId uniqueidentifier,@SessionId uniqueidentifier,
          @BillId uniqueidentifier,@AfterSequence bigint=0,@Take int=50
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION; EXEC Purchasing.RequireSupplierBillRead @ActorId,@SessionId;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Result nvarchar(max),@Next bigint;
            IF @Take IS NULL OR @Take NOT BETWEEN 1 AND 100 OR @AfterSequence IS NULL OR @AfterSequence<0 THROW 51000,'Invalid bill history page.',1;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId) THROW 51004,'Bill unavailable.',1;
            DECLARE @Page TABLE(Sequence bigint PRIMARY KEY,Operation varchar(16),ActorId uniqueidentifier,RecordedAtUtc datetimeoffset,ResultJson nvarchar(max),CanonicalInput nvarchar(max));
            INSERT @Page SELECT TOP(@Take+1) Sequence,Operation,ActorId,RecordedAtUtc,ResultJson,CanonicalInput FROM Purchasing.SupplierBillReceipts
              WHERE TenantId=@TenantId AND BillId=@BillId AND Sequence>@AfterSequence ORDER BY Sequence;
            IF (SELECT COUNT(*) FROM @Page)>@Take SELECT @Next=MAX(Sequence) FROM (SELECT TOP(@Take) Sequence FROM @Page ORDER BY Sequence) p;
            SET @Result=(SELECT JSON_QUERY(COALESCE((SELECT TOP(@Take) p.Sequence sequence,p.Operation operation,p.ActorId actorId,p.RecordedAtUtc recordedAtUtc,
                JSON_QUERY(p.ResultJson) result,JSON_QUERY(p.CanonicalInput) command,
                JSON_QUERY((SELECT r.Id revisionId,r.PurchaseOrderRevision purchaseOrderRevision,r.SupplierName supplierName,
                  r.ActorId actorId,r.RecordedAtUtc recordedAtUtc,JSON_QUERY(r.Payload) payload FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) revision,
                JSON_QUERY((SELECT v.Id reviewId,v.PurchaseOrderRevision purchaseOrderRevision,CONVERT(varchar(18),v.PurchaseOrderVersion,1) purchaseOrderVersion,
                    v.ActorId actorId,v.Rationale rationale,v.RecordedAtUtc recordedAtUtc,JSON_QUERY(v.Resolutions) resolutions,
                    JSON_QUERY(COALESCE((SELECT JSON_VALUE(j.value,'$.documentId') documentId,JSON_VALUE(j.value,'$.revisionId') revisionId,
                        JSON_VALUE(j.value,'$.digest') digest,CONVERT(bigint,JSON_VALUE(j.value,'$.length')) length,JSON_VALUE(j.value,'$.label') label,
                        CONVERT(bit,CASE WHEN d.Id IS NOT NULL AND d.RemovedAtUtc IS NULL AND s.State=1 AND a.Id IS NOT NULL AND a.DeletedAtUtc IS NULL
                          AND NOT EXISTS(SELECT 1 FROM Storage.RecoveryFiles f WHERE f.TenantId=@TenantId AND f.RevisionId=s.Id) THEN 1 ELSE 0 END) available
                      FROM OPENJSON(v.EvidenceJson) j
                      LEFT JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@TenantId AND d.Id=CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId'))
                      LEFT JOIN Storage.Revisions s ON s.TenantId=@TenantId AND s.Id=CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId'))
                      LEFT JOIN Storage.Attachments a ON a.TenantId=@TenantId AND a.Id=d.AttachmentId ORDER BY d.Id FOR JSON PATH),N'[]')) evidence
                  FROM Purchasing.SupplierBillReviews v WHERE v.TenantId=@TenantId AND v.BillId=@BillId AND v.RevisionId=r.Id
                    AND v.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(p.ResultJson,'$.reviewId')) FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) review
              FROM @Page p JOIN Purchasing.SupplierBillRevisions r ON r.TenantId=@TenantId AND r.BillId=@BillId
                AND r.Id=CONVERT(uniqueidentifier,JSON_VALUE(p.ResultJson,'$.revisionId')) ORDER BY p.Sequence FOR JSON PATH),N'[]')) items,
                @Next nextSequence FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
            COMMIT; SELECT @Result ResultJson;
          END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
