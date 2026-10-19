-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.RequireSupplierCashComposition
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,
  @ChildKind nvarchar(40),@EntityId uniqueidentifier,@Details nvarchar(max),
  @GroupId uniqueidentifier OUTPUT,@CorrectionId uniqueidentifier OUTPUT,@RecordedAtUtc datetimeoffset OUTPUT,@Plan nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),
    @Canonical nvarchar(max),@Root uniqueidentifier,@Operation nvarchar(40),@RootCorrection uniqueidentifier;
  IF @@TRANCOUNT=0 OR @Tenant IS NULL OR @OwnerRequestId IS NULL OR @EntityId IS NULL
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@Tenant),'Transaction')<>'Exclusive'
    THROW 51009,'Cash composition requires its owner transaction.',1;
  SET @GroupId=NULL; SET @CorrectionId=NULL; SET @RecordedAtUtc=NULL; SET @Plan=NULL;
  SELECT @GroupId=receipt.GroupId,@RecordedAtUtc=receipt.RecordedAtUtc,@Plan=JSON_QUERY(receipt.ResultJson,'$.plan'),
    @Canonical=receipt.CanonicalInput,@Operation=receipt.Operation,@Root=financialGroup.SourceId
    FROM Purchasing.SupplierFinancialReceipts receipt
    JOIN Purchasing.SupplierFinancialGroups financialGroup ON financialGroup.TenantId=receipt.TenantId AND financialGroup.Id=receipt.GroupId
      AND financialGroup.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(receipt.CanonicalInput,'$.sourceId'))
      AND financialGroup.RecordedAtUtc=receipt.RecordedAtUtc
      AND ((financialGroup.Operation='CorrectRefund' AND receipt.Operation='CorrectSupplierRefund')
        OR (financialGroup.Operation='CorrectRepayment' AND receipt.Operation='CorrectSupplierRefundRepayment'))
    WHERE receipt.TenantId=@Tenant AND receipt.RequestId=@OwnerRequestId AND receipt.ActorId=@ActorId
      AND JSON_VALUE(receipt.ResultJson,'$.state')='pending';
  IF @GroupId IS NULL OR @Plan IS NULL OR @RecordedAtUtc IS NULL
    OR JSON_VALUE(@Canonical,'$.expectedPlanFingerprint')<>JSON_VALUE(@Plan,'$.planFingerprint')
    OR CONVERT(varbinary(max),JSON_MODIFY(@Canonical,'$.expectedPlanFingerprint',NULL))<>CONVERT(varbinary(max),JSON_QUERY(@Plan,'$.command'))
    THROW 51004,'An exact actor-bound pending cash correction is required.',1;
  EXEC Purchasing.RequireSupplierCashCorrectionAuthority @ActorId,@SessionId,@Operation,@Canonical;
  SELECT @RootCorrection=correction.Id FROM Purchasing.SupplierSourceCorrections correction
    WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.SourceId=@Root
      AND correction.SourceKind IN('SupplierRefund','SupplierRefundRepayment') AND correction.OriginalCashSourceId=@Root
      AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
      AND correction.SourceRevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].sourceRevisionId'))
      AND correction.OriginalSourceVersion=TRY_CONVERT(binary(8),JSON_VALUE(@Plan,'$.sources[0].version'),1);
  IF @RootCorrection IS NULL THROW 51004,'The pending cash root membership is unavailable.',1;
  IF @ChildKind='ReverseCash'
    SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction CROSS APPLY OPENJSON(@Plan,'$.sources') source
      WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId AND correction.SourceId=@EntityId
        AND correction.SourceKind=JSON_VALUE(source.value,'$.sourceKind') AND correction.OriginalCashSourceId=@EntityId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))=@EntityId
        AND correction.SourceRevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceRevisionId'))
        AND correction.OriginalSourceVersion=TRY_CONVERT(binary(8),JSON_VALUE(source.value,'$.version'),1)
        AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
        AND TRY_CONVERT(date,JSON_VALUE(source.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23);
  ELSE IF @ChildKind='ReplacementCash'
    SELECT @CorrectionId=correction.Id FROM Purchasing.SupplierSourceCorrections correction CROSS APPLY OPENJSON(@Plan,'$.sources') source
      WHERE correction.TenantId=@Tenant AND correction.GroupId=@GroupId
        AND correction.SourceKind=JSON_VALUE(source.value,'$.sourceKind') AND correction.OriginalCashSourceId=correction.SourceId
        AND correction.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))
        AND correction.ReplacementSourceId=@EntityId AND correction.ReplacementSourceRevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementRevisionId'))
        AND correction.ActorId=@ActorId AND correction.RecordedAtUtc=@RecordedAtUtc
        AND CONVERT(varbinary(max),@Details)=CONVERT(varbinary(max),JSON_QUERY(source.value,'$.replacement'));
  ELSE IF @ChildKind IN('ReverseRelease','Application','ReverseApplication')
  BEGIN
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current allocation authority is required for clearing dependencies.',1; THROW; END CATCH;
    IF @ChildKind='ReverseRelease' AND EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reverseReleases') released
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(released.value,'$.journalId'))=@EntityId
        AND TRY_CONVERT(date,JSON_VALUE(released.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23))
      SET @CorrectionId=@RootCorrection;
    IF @ChildKind='ReverseApplication' AND EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.applications') app
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(app.value,'$.applicationId'))=@EntityId
        AND TRY_CONVERT(date,JSON_VALUE(app.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23))
      SET @CorrectionId=@RootCorrection;
    IF @ChildKind='Application' AND EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reapplications') app
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(app.value,'$.fundingItemId'))=@EntityId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(app.value,'$.debtItemId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Details,'$.debtItemId'))
        AND TRY_CONVERT(decimal(28,4),JSON_VALUE(app.value,'$.amount'))=TRY_CONVERT(decimal(28,4),JSON_VALUE(@Details,'$.amount'))
        AND TRY_CONVERT(date,JSON_VALUE(app.value,'$.postingDate'),23)=TRY_CONVERT(date,JSON_VALUE(@Details,'$.postingDate'),23))
      SET @CorrectionId=@RootCorrection;
  END;
  IF @CorrectionId IS NULL THROW 51004,'The exact cash source-owned child composition is unavailable.',1;
END;
