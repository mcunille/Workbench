-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Purchasing.ValidateBillEvidence @TenantId uniqueidentifier,@PurchaseOrderId uniqueidentifier,
  @Payload nvarchar(max),@Evidence nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 THROW 51000,'Source transaction required.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.documents')) AND
    (EXISTS(SELECT 1 FROM Security.BlobRecoveryState WHERE IsPending=1) OR EXISTS(SELECT 1 FROM Security.WorkbenchRestorePending WHERE IsPending=1))
    THROW 51004,'Evidence recovery verification is pending.',1;
  IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK)
    WHERE TenantId=@TenantId AND Id=@PurchaseOrderId AND IsDeleted=0 AND State='Ordered') THROW 51004,'Evidence purchase unavailable.',1;
  IF EXISTS(SELECT JSON_VALUE(value,'$.documentId') FROM OPENJSON(@Payload,'$.documents') GROUP BY JSON_VALUE(value,'$.documentId') HAVING COUNT(*)>1)
    OR EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.documents') WHERE type<>5
      OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.documentId')) IS NULL OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.revisionId')) IS NULL)
    THROW 51000,'Invalid evidence document identities.',1;
  DECLARE @Documents TABLE(DocumentId uniqueidentifier PRIMARY KEY,AttachmentId uniqueidentifier,RevisionId uniqueidentifier);
  INSERT @Documents SELECT d.Id,d.AttachmentId,d.RevisionId FROM OPENJSON(@Payload,'$.documents') j
    JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@TenantId AND d.OrderId=@PurchaseOrderId AND d.RemovedAtUtc IS NULL
      AND d.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId')) AND d.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId'));
  IF (SELECT COUNT(*) FROM @Documents)<>(SELECT COUNT(*) FROM OPENJSON(@Payload,'$.documents')) THROW 51004,'Evidence document unavailable.',1;
  DECLARE @Document uniqueidentifier,@Attachment uniqueidentifier,@Revision uniqueidentifier,@Digest varchar(64),@Length bigint,@Media nvarchar(100);
  DECLARE evidence_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT DocumentId,AttachmentId,RevisionId FROM @Documents ORDER BY AttachmentId,RevisionId,DocumentId;
  OPEN evidence_cursor; FETCH NEXT FROM evidence_cursor INTO @Document,@Attachment,@Revision;
  WHILE @@FETCH_STATUS=0
  BEGIN
    IF NOT EXISTS(SELECT 1 FROM Storage.Attachments WITH(UPDLOCK,HOLDLOCK,INDEX(PK_Attachments)) WHERE TenantId=@TenantId AND Id=@Attachment
      AND CurrentRevisionId=@Revision AND DeletedAtUtc IS NULL) THROW 51004,'Evidence attachment unavailable.',1;
    SET @Digest=NULL;
    SELECT @Digest=Sha256,@Length=Length,@Media=MediaType FROM Storage.Revisions WITH(UPDLOCK,HOLDLOCK)
      WHERE TenantId=@TenantId AND AttachmentId=@Attachment AND Id=@Revision AND State=1;
    IF @Digest IS NULL OR NOT EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND Id=@Document
        AND Sha256=@Digest AND Length=@Length AND MediaType=@Media)
      OR EXISTS(SELECT 1 FROM Storage.RecoveryFiles WHERE TenantId=@TenantId AND RevisionId=@Revision)
      THROW 51004,'Evidence revision unavailable or inconsistent.',1;
    FETCH NEXT FROM evidence_cursor INTO @Document,@Attachment,@Revision;
  END;
  CLOSE evidence_cursor; DEALLOCATE evidence_cursor;
  SET @Evidence=(SELECT d.Id documentId,d.RevisionId revisionId,d.Sha256 digest,d.Length length,d.Label label
    FROM @Documents selected JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=@TenantId AND d.Id=selected.DocumentId ORDER BY d.Id FOR JSON PATH);
  SET @Evidence=COALESCE(@Evidence,N'[]');
END;
