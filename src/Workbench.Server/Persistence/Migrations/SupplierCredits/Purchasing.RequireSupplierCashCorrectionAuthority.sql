-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.RequireSupplierCashCorrectionAuthority
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Operation nvarchar(40),@Canonical nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @RootKind varchar(32)=CASE @Operation WHEN N'CorrectSupplierRefund' THEN 'SupplierRefund'
    WHEN N'CorrectSupplierRefundRepayment' THEN 'SupplierRefundRepayment' END;
  IF @RootKind IS NULL THROW 51000,'A fixed cash correction family is required.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions')
    WHERE JSON_VALUE(value,'$.sourceKind') NOT IN('SupplierRefund','SupplierRefundRepayment'))
    THROW 51004,'An unsupported cash source decision cannot borrow another family.',1;
  DECLARE @Required TABLE(Permission nvarchar(100) PRIMARY KEY);
  INSERT @Required SELECT DISTINCT CASE Kind WHEN 'SupplierRefund' THEN N'SupplierRefundsCorrect' ELSE N'SupplierRefundRepaymentsCorrect' END
    FROM(SELECT @RootKind Kind UNION ALL SELECT JSON_VALUE(value,'$.sourceKind') FROM OPENJSON(@Canonical,'$.sourceDecisions')) family;
  INSERT @Required SELECT DISTINCT CASE Kind WHEN 'SupplierRefund' THEN N'SupplierRefundsRecord' ELSE N'SupplierRefundRepaymentsRecord' END
    FROM(SELECT @RootKind Kind,JSON_QUERY(@Canonical,'$.replacement') Replacement
      UNION ALL SELECT JSON_VALUE(value,'$.sourceKind'),JSON_QUERY(value,'$.replacement') FROM OPENJSON(@Canonical,'$.sourceDecisions')) family
    WHERE Replacement IS NOT NULL;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.reapplications'))
    INSERT @Required VALUES(N'SupplierAllocationsManage');
  DECLARE @Permission nvarchar(100);
  DECLARE permissions CURSOR LOCAL FAST_FORWARD FOR SELECT Permission FROM @Required ORDER BY Permission;
  OPEN permissions; FETCH NEXT FROM permissions INTO @Permission;
  WHILE @@FETCH_STATUS=0
  BEGIN
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,@Permission; END TRY
    BEGIN CATCH
      IF ERROR_NUMBER()=50903 THROW 51003,'Every affected cash family requires its current correction and actual successor recording authority.',1;
      THROW;
    END CATCH;
    FETCH NEXT FROM permissions INTO @Permission;
  END;
  CLOSE permissions; DEALLOCATE permissions;
END;
