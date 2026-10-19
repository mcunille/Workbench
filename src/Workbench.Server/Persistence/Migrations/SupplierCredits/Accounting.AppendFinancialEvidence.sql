ALTER PROCEDURE Accounting.AppendFinancialEvidence @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRY
    BEGIN TRANSACTION;
    IF @Command IS NULL OR ISJSON(@Command,OBJECT)<>1 OR DATALENGTH(@Command)>262144
      OR @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid evidence addition envelope.',1;
    IF (SELECT COUNT(*) FROM OPENJSON(@Command))<>9
      OR EXISTS(SELECT [key] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@Command) GROUP BY [key] COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
      OR EXISTS(SELECT 1 FROM OPENJSON(@Command) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key]))
        OR [key] COLLATE Latin1_General_100_BIN2 NOT IN('schemaVersion','ownerKind','ownerId','ownerRevisionId','expectedEvidenceVersion','documentId','revisionId','replacesLinkId','reason')
        OR ([key]='schemaVersion' AND (type<>2 OR value<>'1'))
        OR ([key]='replacesLinkId' AND type NOT IN(0,1)) OR ([key] NOT IN('schemaVersion','replacesLinkId') AND type<>1)
        OR ([key]='reason' AND DATALENGTH(value) NOT BETWEEN 2 AND 4000)
        OR ([key]='ownerKind' AND value COLLATE Latin1_General_100_BIN2 NOT IN('SupplierBill','SupplierPayment','PurchaseRecognition','SupplierCredit','SupplierRefund','SupplierRefundRepayment'))
        OR ([key]='expectedEvidenceVersion' AND (DATALENGTH(value)<>36 OR TRY_CONVERT(binary(8),value,1) IS NULL))
        OR ([key] IN('ownerId','ownerRevisionId','documentId','revisionId','replacesLinkId') AND type<>0
          AND (DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL OR TRY_CONVERT(uniqueidentifier,value)='00000000-0000-0000-0000-000000000000')))
      THROW 51000,'Invalid evidence addition fields.',1;
    DECLARE @Reason nvarchar(2000)=TRIM(NCHAR(9)+NCHAR(10)+NCHAR(13)+NCHAR(32)+NCHAR(160)+NCHAR(8195) FROM JSON_VALUE(@Command,'$.reason'));
    IF LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM @Reason))=0
      THROW 51000,'A reason is required.',1;
    DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Lock int,
      @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),SESSION_CONTEXT(N'TenantId')),
      @Kind varchar(32)=JSON_VALUE(@Command,'$.ownerKind'),@Owner uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.ownerId')),
      @OwnerRevision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.ownerRevisionId')),
      @Document uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.documentId')),@Revision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.revisionId')),
      @Replaces uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Command,'$.replacesLinkId')),
      @Expected binary(8)=CONVERT(binary(8),JSON_VALUE(@Command,'$.expectedEvidenceVersion'),1),@Set uniqueidentifier,@Po uniqueidentifier,@Permission nvarchar(100),@Result nvarchar(max);
    EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @Lock<0 THROW 51009,'Evidence is being changed. Retry.',1;
    SELECT @Set=Id,@Po=PurchaseOrderId,@Permission=MutationPermission FROM Accounting.FinancialEvidenceSets
      WHERE TenantId=@TenantId AND OwnerKind=@Kind AND OwnerId=@Owner AND OwnerRevisionId=@OwnerRevision;
    IF @Set IS NULL THROW 51004,'Evidence source unavailable.',1;
    IF @Permission IS NULL THROW 51003,'Source mutation authority has no authenticated provenance.',1;
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,@Permission; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current source mutation authority required.',1; THROW; END CATCH;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Po AND IsDeleted=0 AND State='Ordered')
      THROW 51004,'Purchase document authority unavailable.',1;
    DECLARE @Canonical nvarchar(max)=(SELECT 1 schemaVersion,@Kind ownerKind,@Owner ownerId,@OwnerRevision ownerRevisionId,
      CONVERT(varchar(18),@Expected,1) expectedEvidenceVersion,@Document documentId,@Revision revisionId,@Replaces replacesLinkId,@Reason reason
      FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
    IF EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
    BEGIN
      IF NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId AND Operation='Append' AND ActorId=@ActorId
        AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical)) THROW 51009,'Request identity has different content.',1;
      SELECT @Result=ResultJson FROM Accounting.FinancialEvidenceReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
      COMMIT; SELECT @Result ResultJson; RETURN;
    END;
    IF NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@Set AND RowVersion=@Expected)
      THROW 51009,'Evidence changed. Refresh before adding a document.',1;
    IF @Replaces IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND Id=@Replaces AND EvidenceSetId=@Set)
      THROW 51004,'Replacement predecessor is not owned by this source.',1;
    DECLARE @Link uniqueidentifier,@Addition uniqueidentifier=NEWID(),@Now datetimeoffset=SYSUTCDATETIME();
    EXEC Accounting.AcquireFinancialEvidenceLink @Set,@Document,@Revision,NULL,NULL,NULL,@Link OUTPUT;
    UPDATE Accounting.FinancialEvidenceSets SET Id=Id WHERE TenantId=@TenantId AND Id=@Set;
    SET @Result=(SELECT @Set evidenceSetId,@Link linkId,@Addition additionId,
      CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) evidenceVersion FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@Set FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    INSERT Accounting.FinancialEvidenceReceipts(TenantId,RequestId,Operation,ActorId,EvidenceSetId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
      VALUES(@TenantId,@RequestId,'Append',@ActorId,@Set,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);
    INSERT Accounting.FinancialEvidenceAdditions(TenantId,Id,EvidenceSetId,LinkId,ReplacesLinkId,RequestId,ActorId,Reason,RecordedAtUtc)
      VALUES(@TenantId,@Addition,@Set,@Link,@Replaces,@RequestId,@ActorId,@Reason,@Now);
    COMMIT; SELECT @Result ResultJson;
  END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
