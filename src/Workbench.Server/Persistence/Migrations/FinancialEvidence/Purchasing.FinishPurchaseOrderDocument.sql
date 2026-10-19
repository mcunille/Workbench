-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Purchasing].[FinishPurchaseOrderDocument] @RequestId uniqueidentifier,@Published bit
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
  IF NOT EXISTS(SELECT 1 FROM Tenancy.Tenants WITH(HOLDLOCK) WHERE Id=@TenantId AND IsEnabled=1)
    SET @Conflict=1;
  IF NOT EXISTS(SELECT 1 FROM [Identity].Users WITH(HOLDLOCK) WHERE TenantId=@TenantId AND Id=@ActorUserId AND State=1)
    SET @Conflict=1;
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
  IF @Kind=2
BEGIN
  SELECT @AttachmentId=AttachmentId FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND Id=@DocumentId;
  DECLARE @HeldAttachment uniqueidentifier;
  SELECT @HeldAttachment=Id FROM Storage.Attachments WITH(UPDLOCK,HOLDLOCK,INDEX(PK_Attachments)) WHERE TenantId=@TenantId AND Id=@AttachmentId;
  IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId)
    SET @Conflict=1;
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
