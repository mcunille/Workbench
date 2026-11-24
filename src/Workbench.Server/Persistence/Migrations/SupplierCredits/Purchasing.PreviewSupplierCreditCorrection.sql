-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PreviewSupplierCreditCorrection
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT OFF;
  DECLARE @OwnTransaction bit=CASE WHEN @@TRANCOUNT=0 THEN 1 ELSE 0 END;
  BEGIN TRY
    IF @OwnTransaction=1 BEGIN TRAN;
    DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Resource nvarchar(255),@Lock int,@Plan nvarchar(max);
    IF @Tenant IS NULL THROW 51003,'Current credit correction authority is required.',1;
    SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
    EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @Lock<0 THROW 51009,'Accounting is being changed. Retry.',1;
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierCreditsCorrect'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current credit correction authority is required.',1; THROW; END CATCH;
    EXEC Purchasing.PlanSupplierSourceCorrection @Tenant,N'CorrectSupplierCredit',@Command,@Plan OUTPUT;
    IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.sources') WHERE JSON_QUERY(value,'$.replacement') IS NOT NULL)
    BEGIN
      BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierCreditsPost'; END TRY
      BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current successor credit posting authority is required.',1; THROW; END CATCH;
    END;
    IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.applications')) OR EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reapplications')) OR EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.settlements'))
    BEGIN
      BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
      BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current allocation authority is required.',1; THROW; END CATCH;
    END;
    IF @OwnTransaction=1 COMMIT;
    SELECT @Plan ResultJson;
  END TRY BEGIN CATCH IF @OwnTransaction=1 AND @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
