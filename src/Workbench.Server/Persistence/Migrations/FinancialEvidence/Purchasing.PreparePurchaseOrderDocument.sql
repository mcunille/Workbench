-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Purchasing].[PreparePurchaseOrderDocument]
  @OrderId uniqueidentifier,@RequestId uniqueidentifier,
  @ExpectedOrderVersion varbinary(max),@Kind int,
  @DocumentId uniqueidentifier=NULL,@ExpectedDocumentVersion varbinary(max)=NULL,
  @Label nvarchar(max)=NULL,@MediaType nvarchar(max)=NULL,@Extension nvarchar(max)=NULL,
  @Length bigint=NULL,@Sha256 nvarchar(max)=NULL,@ProviderAlias nvarchar(max)=NULL,@ActorUserId uniqueidentifier
AS
BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  DECLARE @CurrentTenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
  IF @CurrentTenant IS NULL OR NOT EXISTS(SELECT 1 FROM Security.fn_tenant_access(@CurrentTenant))
    OR NOT EXISTS(SELECT 1 FROM [Identity].Users WHERE TenantId=@CurrentTenant AND Id=@ActorUserId AND State=1)
    OR NOT EXISTS(SELECT 1 FROM Tenancy.Tenants WHERE Id=@CurrentTenant AND IsEnabled=1)
    THROW 50403,'Current tenant authority required.',1;
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
  IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId AND Operation='Dispose')
  THROW 50077,'Use the authorized disposal operation to recover this request.',1;
IF EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocumentOperations WHERE TenantId=@TenantId AND RequestId=@RequestId)
  BEGIN
    IF NOT EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderDocumentOperations WHERE TenantId=@TenantId AND RequestId=@RequestId
      AND ActorUserId=@ActorUserId AND OrderId=@OrderId AND Kind=@Kind
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
IF @Kind=2
BEGIN
  SELECT @AttachmentId=AttachmentId FROM Purchasing.PurchaseOrderDocuments WHERE TenantId=@TenantId AND Id=@DocumentId;
  EXEC Storage.RequireFinancialEvidenceDeletion @TenantId,@AttachmentId,0;
  IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND AttachmentId=@AttachmentId)
    THROW 51011,'Financial evidence is retained. Use authorized retention disposal.',1;
END;
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
