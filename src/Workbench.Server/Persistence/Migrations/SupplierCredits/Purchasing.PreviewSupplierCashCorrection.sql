-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PreviewSupplierCashCorrection
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Command nvarchar(max),@Operation nvarchar(40)
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRY
    BEGIN TRAN;
    DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Resource nvarchar(255),@Lock int,@Plan nvarchar(max);
    IF @Tenant IS NULL THROW 51003,'Current cash correction authority is required.',1;
    SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
    EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @Lock<0 THROW 51009,'Accounting is being changed. Retry.',1;
    EXEC Purchasing.PlanSupplierCashCorrection @Tenant,@ActorId,@SessionId,@Operation,@Command,@Plan OUTPUT;
    ROLLBACK;
    SELECT @Plan ResultJson;
  END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
