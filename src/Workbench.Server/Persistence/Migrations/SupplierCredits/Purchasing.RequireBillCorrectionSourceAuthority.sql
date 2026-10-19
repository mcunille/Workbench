-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.RequireBillCorrectionSourceAuthority
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Canonical nvarchar(max)
WITH EXECUTE AS OWNER
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),
    @Bill uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId'));
  IF @@TRANCOUNT=0 OR @Tenant IS NULL OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@Tenant),'Transaction')<>'Exclusive'
    THROW 51009,'Current source authority requires its owner transaction.',1;
  DECLARE @Owners TABLE(EventId uniqueidentifier PRIMARY KEY,PostingPermission nvarchar(100),CorrectionPermission nvarchar(100),
    Replacement bit,ValidatorSchema sysname,ValidatorName sysname,DefinitionSha256 binary(32));
  INSERT @Owners SELECT original.Id,registered.PostingPermission,registered.CorrectionPermission,
      CASE WHEN original.Side='Invoice' THEN CASE WHEN JSON_QUERY(@Canonical,'$.replacement') IS NULL THEN 0 ELSE 1 END
        ELSE CASE WHEN JSON_QUERY(decision.value,'$.replacement') IS NULL THEN 0 ELSE 1 END END,
      registered.ValidatorSchema,registered.ValidatorName,registered.DefinitionSha256
    FROM Purchasing.SupplierBillPostingEvents posting
    JOIN Purchasing.RecognitionSideEvents invoice ON invoice.TenantId=posting.TenantId AND invoice.Id=posting.EventId
    JOIN Purchasing.RecognitionSideEvents original ON original.TenantId=invoice.TenantId AND original.UnitId=invoice.UnitId
    LEFT JOIN Purchasing.RecognitionSourceOwners registered WITH(UPDLOCK,HOLDLOCK)
      ON registered.PostingPermission=original.EvidenceMutationPermission COLLATE Latin1_General_100_BIN2
    OUTER APPLY(SELECT value FROM OPENJSON(@Canonical,'$.sourceDecisions') WHERE JSON_VALUE(value,'$.sourceKind')='PurchaseRecognition'
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId'))=original.Id) decision
    WHERE posting.TenantId=@Tenant AND posting.BillId=@Bill;
  IF EXISTS(SELECT 1 FROM @Owners WHERE CorrectionPermission IS NULL)
    THROW 51004,'An affected recognition source has no complete registered owner.',1;
  DECLARE @Posting nvarchar(100),@Correction nvarchar(100),@Replacement bit,@Schema sysname,@Name sysname,@Hash binary(32);
  DECLARE owners CURSOR LOCAL FAST_FORWARD FOR SELECT PostingPermission,CorrectionPermission,Replacement,ValidatorSchema,ValidatorName,DefinitionSha256
    FROM @Owners ORDER BY EventId;
  OPEN owners; FETCH NEXT FROM owners INTO @Posting,@Correction,@Replacement,@Schema,@Name,@Hash;
  WHILE @@FETCH_STATUS=0
  BEGIN
    BEGIN TRY
      EXEC Accounting.RequirePermission @ActorId,@SessionId,@Correction;
      IF @Replacement=1 EXEC Accounting.RequirePermission @ActorId,@SessionId,@Posting;
    END TRY BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current upstream source authority is required.',1; THROW; END CATCH;
    DECLARE @CurrentHash binary(32)=HASHBYTES('SHA2_256',CONVERT(varbinary(max),OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@Schema)+N'.'+QUOTENAME(@Name),N'P'))));
    IF @CurrentHash IS NULL OR @Hash<>@CurrentHash
      THROW 51009,'The registered source owner module changed.',1;
    FETCH NEXT FROM owners INTO @Posting,@Correction,@Replacement,@Schema,@Name,@Hash;
  END;
  CLOSE owners; DEALLOCATE owners;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') WHERE JSON_VALUE(value,'$.sourceKind')='SupplierCredit')
    OR EXISTS(SELECT 1 FROM Purchasing.SupplierBillPostingEvents posting JOIN Purchasing.RecognitionSideEvents invoice
      ON invoice.TenantId=posting.TenantId AND invoice.Id=posting.EventId
      JOIN Purchasing.RecognitionSideEvents original ON original.TenantId=invoice.TenantId AND original.UnitId=invoice.UnitId
      JOIN Purchasing.SupplierSourceCorrectionMembers adjustment ON adjustment.TenantId=original.TenantId
        AND adjustment.RecognitionEventId=original.Id AND adjustment.Kind='RecognitionAdjustment'
      WHERE posting.TenantId=@Tenant AND posting.BillId=@Bill)
  BEGIN
    BEGIN TRY
      EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierCreditsCorrect';
      IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') WHERE JSON_VALUE(value,'$.sourceKind')='SupplierCredit' AND JSON_QUERY(value,'$.replacement') IS NOT NULL)
        EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierCreditsPost';
    END TRY BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current dependent credit authority is required.',1; THROW; END CATCH;
  END;
END;
