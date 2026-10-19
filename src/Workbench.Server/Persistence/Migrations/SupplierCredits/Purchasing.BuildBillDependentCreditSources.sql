-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.BuildBillDependentCreditSources
  @TenantId uniqueidentifier,@Canonical nvarchar(max),@Units nvarchar(max),@Credits nvarchar(max) OUTPUT,
  @Positions nvarchar(max) OUTPUT,@Adjustments nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Credit dependency planning requires its bill owner transaction.',1;
  DECLARE @Affected TABLE(Id uniqueidentifier PRIMARY KEY,Version varchar(18),Replacement nvarchar(max));
  INSERT @Affected SELECT DISTINCT credit.Id,CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),JSON_QUERY(decision.value,'$.replacement')
    FROM Purchasing.SupplierCreditClaims claim
    JOIN Purchasing.SupplierCredits credit WITH(UPDLOCK,HOLDLOCK) ON credit.TenantId=claim.TenantId AND credit.Id=claim.CreditId
    JOIN Purchasing.SupplierCreditVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=credit.TenantId AND version.CreditId=credit.Id
    OUTER APPLY(SELECT value FROM OPENJSON(@Canonical,'$.sourceDecisions') WHERE JSON_VALUE(value,'$.sourceKind')='SupplierCredit'
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId'))=credit.Id) decision
    WHERE claim.TenantId=@TenantId AND claim.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId'))
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections corrected WHERE corrected.TenantId=credit.TenantId
        AND corrected.SourceKind='SupplierCredit' AND corrected.SourceId=credit.Id);
  IF EXISTS(SELECT 1 FROM @Affected affected WHERE NOT EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') decision
      WHERE JSON_VALUE(decision.value,'$.sourceKind')='SupplierCredit' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))=affected.Id
        AND JSON_VALUE(decision.value,'$.expectedSourceVersion')=affected.Version))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') decision WHERE JSON_VALUE(decision.value,'$.sourceKind')='SupplierCredit'
      AND NOT EXISTS(SELECT 1 FROM @Affected affected WHERE affected.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))))
    THROW 51009,'Every active dependent credit requires an explicit complete current source decision.',1;
  DECLARE @Resolved TABLE(Id uniqueidentifier PRIMARY KEY,Input nvarchar(max));
  DECLARE @Credit uniqueidentifier,@Version varchar(18),@Intent nvarchar(max),@ResolvedSource nvarchar(max);
  DECLARE credits CURSOR LOCAL FAST_FORWARD FOR SELECT Id,Version FROM @Affected ORDER BY Id;
  OPEN credits; FETCH NEXT FROM credits INTO @Credit,@Version;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @Intent=JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(@Canonical,'$.operation','CorrectSupplierCredit'),'$.sourceId',CONVERT(nvarchar(36),@Credit)),'$.expectedSourceVersion',@Version);
    SET @Intent=JSON_MODIFY(JSON_MODIFY(@Intent,'$.sourceDecisions',JSON_QUERY(N'[]')),'$.reapplications',JSON_QUERY(N'[]'));
    SET @Intent=JSON_MODIFY(@Intent,'strict $.replacement',NULL);
    EXEC Purchasing.BuildSupplierCreditCorrectionSources @TenantId,@Intent,@ResolvedSource OUTPUT;
    IF (SELECT COUNT(*) FROM OPENJSON(@ResolvedSource))<>1 THROW 51004,'A complete credit source owner is required.',1;
    DECLARE @SourceInput nvarchar(max)=JSON_MODIFY(JSON_QUERY(@ResolvedSource,'$[0]'),'$.sourceKind','SupplierCredit'),@RequestedReplacement nvarchar(max);
    SELECT @RequestedReplacement=Replacement FROM @Affected WHERE Id=@Credit;
    IF @RequestedReplacement IS NOT NULL SET @SourceInput=JSON_MODIFY(@SourceInput,'$.replacement',JSON_QUERY(@RequestedReplacement));
    INSERT @Resolved VALUES(@Credit,@SourceInput);
    FETCH NEXT FROM credits INTO @Credit,@Version;
  END;
  CLOSE credits; DEALLOCATE credits;
  SET @Credits=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),Input),N',') WITHIN GROUP(ORDER BY Id),N'')+N']' FROM @Resolved);
  IF EXISTS(SELECT 1 FROM OPENJSON(@Credits) WHERE TRY_CONVERT(date,JSON_VALUE(value,'$.sourcePostingDate'),23)>TRY_CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23))
    THROW 51009,'The bill correction cannot precede a dependent credit posting.',1;
  SET @Positions=N'[]'; SET @Adjustments=N'[]';
END;
