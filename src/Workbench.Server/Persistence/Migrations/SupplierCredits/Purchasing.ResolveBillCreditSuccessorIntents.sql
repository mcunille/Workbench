-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ResolveBillCreditSuccessorIntents
  @TenantId uniqueidentifier,@Canonical nvarchar(max),@Credits nvarchar(max),@ResolvedCredits nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Credit successor validation requires its bill owner transaction.',1;
  DECLARE @Sources TABLE(Id uniqueidentifier PRIMARY KEY,Input nvarchar(max));
  INSERT @Sources SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),value FROM OPENJSON(@Credits);
  DECLARE @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
    @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Scale int,@Policy nvarchar(max),@Id uniqueidentifier,@Input nvarchar(max),
    @Replacement nvarchar(max),@ReplacementCanonical nvarchar(max),@Documents nvarchar(max),@Evidence nvarchar(max),@Reference nvarchar(200),@Date date;
  SELECT @Policy=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Version=@Config;
  SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Policy,'$.policies.scale'));
  IF @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 THROW 51009,'Accounting configuration changed.',1;
  DECLARE replacements CURSOR LOCAL FAST_FORWARD FOR SELECT Id,Input FROM @Sources WHERE JSON_QUERY(Input,'$.replacement') IS NOT NULL ORDER BY Id;
  OPEN replacements; FETCH NEXT FROM replacements INTO @Id,@Input;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @Replacement=JSON_QUERY(@Input,'$.replacement');
    EXEC Purchasing.CanonicalizeSupplierCredit N'PostSupplierCredit',@Replacement,@ReplacementCanonical OUTPUT;
    SET @ReplacementCanonical=JSON_MODIFY(@ReplacementCanonical,'$.effectiveDate',
      (SELECT CONVERT(char(10),EffectiveDate,23) FROM Purchasing.SupplierCredits WHERE TenantId=@TenantId AND Id=@Id));
    SET @Date=CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.postingDate'),23);
    IF TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.purchaseOrderId'))<>@Po
      OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.supplierId'))<>@Supplier
      OR JSON_VALUE(@ReplacementCanonical,'$.currency') COLLATE Latin1_General_100_BIN2<>@Currency
      OR JSON_VALUE(@ReplacementCanonical,'$.expectedConfigurationVersion')<>JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')
      OR JSON_VALUE(@ReplacementCanonical,'$.expectedPurchaseOrderVersion')<>JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion')
      OR @Date<CONVERT(date,JSON_VALUE(@Input,'$.postingDate'),23)
      OR CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.documentDate'),23)>@Date
      OR CONVERT(date,JSON_VALUE(@ReplacementCanonical,'$.effectiveDate'),23)>@Date
      THROW 51000,'A credit successor must preserve complete scope and eligible dates.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierCredits WHERE TenantId=@TenantId AND
      (Id=CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.creditId'))
        OR RevisionId=CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.creditRevisionId'))))
      OR EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems WHERE TenantId=@TenantId AND Id=CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.creditId')))
      OR EXISTS(SELECT 1 FROM OPENJSON(@ReplacementCanonical,'$.claims') proposed JOIN Purchasing.SupplierCreditClaims actual
        ON actual.TenantId=@TenantId AND actual.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(proposed.value,'$.claimId')))
      OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@ReplacementCanonical,'$.creditId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.replacement.post.billId'))
      THROW 51009,'A successor credit requires fresh source, item, revision and claim identities.',1;
    IF CONVERT(decimal(28,4),JSON_VALUE(@ReplacementCanonical,'$.total'))<>ROUND(CONVERT(decimal(28,4),JSON_VALUE(@ReplacementCanonical,'$.total')),@Scale,1)
      OR EXISTS(SELECT 1 FROM OPENJSON(@ReplacementCanonical,'$.claims') WHERE
        CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount'))<>ROUND(CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),@Scale,1))
      OR (SELECT SUM(CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount'))) FROM OPENJSON(@ReplacementCanonical,'$.claims'))
        <>CONVERT(decimal(28,4),JSON_VALUE(@ReplacementCanonical,'$.total'))
      THROW 51000,'Credit successor claims must equal its exact configured-scale total.',1;
    IF @Date<TRY_CONVERT(date,JSON_VALUE(@Policy,'$.policies.plannedStartDate'),23)
      THROW 51004,'A credit successor cannot predate accounting start.',1;
    IF EXISTS(SELECT 1 FROM Accounting.PeriodClosures WHERE TenantId=@TenantId AND PeriodStart=DATEFROMPARTS(YEAR(@Date),MONTH(@Date),1))
      THROW 51009,'The credit successor posting period is closed.',1;
    SET @Reference=Purchasing.NormalizeBillReference(JSON_VALUE(@ReplacementCanonical,'$.reference'));
    IF COALESCE(LEN(@Reference),0)=0 THROW 51000,'A successor credit reference is required.',1;
    IF EXISTS(SELECT Id,RevisionId FROM Purchasing.SupplierCredits WHERE TenantId=@TenantId AND SupplierId=@Supplier AND NormalizedReference=@Reference
      EXCEPT SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditRevisionId'))
        FROM OPENJSON(@ReplacementCanonical,'$.duplicateResolutions'))
      OR EXISTS(SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditRevisionId'))
        FROM OPENJSON(@ReplacementCanonical,'$.duplicateResolutions')
        EXCEPT SELECT Id,RevisionId FROM Purchasing.SupplierCredits WHERE TenantId=@TenantId AND SupplierId=@Supplier AND NormalizedReference=@Reference)
      THROW 51009,'The successor reference requires its exact recorded duplicate resolutions.',1;
    SET @Evidence=JSON_QUERY(@ReplacementCanonical,'$.evidence');
    EXEC Purchasing.ValidateBillEvidence @TenantId,@Po,@Evidence,@Documents OUTPUT;
    SET @Input=JSON_MODIFY(@Input,'$.requestedAllocations',JSON_QUERY(@ReplacementCanonical,'$.allocations'));
    SET @ReplacementCanonical=JSON_MODIFY(@ReplacementCanonical,'$.allocations',JSON_QUERY(N'[]'));
    SET @Input=JSON_MODIFY(@Input,'$.replacement',JSON_QUERY(@ReplacementCanonical));
    SET @Input=JSON_MODIFY(@Input,'$.replacementId',JSON_VALUE(@ReplacementCanonical,'$.creditId'));
    SET @Input=JSON_MODIFY(@Input,'$.replacementRevisionId',JSON_VALUE(@ReplacementCanonical,'$.creditRevisionId'));
    UPDATE @Sources SET Input=@Input WHERE Id=@Id;
    FETCH NEXT FROM replacements INTO @Id,@Input;
  END;
  CLOSE replacements; DEALLOCATE replacements;
  IF EXISTS(SELECT JSON_VALUE(Input,'$.replacementId') FROM @Sources WHERE JSON_QUERY(Input,'$.replacement') IS NOT NULL
      GROUP BY JSON_VALUE(Input,'$.replacementId') HAVING COUNT(*)>1)
    OR EXISTS(SELECT JSON_VALUE(Input,'$.replacementRevisionId') FROM @Sources WHERE JSON_QUERY(Input,'$.replacement') IS NOT NULL
      GROUP BY JSON_VALUE(Input,'$.replacementRevisionId') HAVING COUNT(*)>1)
    OR EXISTS(SELECT Purchasing.NormalizeBillReference(JSON_VALUE(Input,'$.replacement.reference')) FROM @Sources WHERE JSON_QUERY(Input,'$.replacement') IS NOT NULL
      GROUP BY Purchasing.NormalizeBillReference(JSON_VALUE(Input,'$.replacement.reference')) HAVING COUNT(*)>1)
    OR EXISTS(SELECT JSON_VALUE(claim.value,'$.claimId') FROM @Sources source CROSS APPLY OPENJSON(source.Input,'$.replacement.claims') claim
      GROUP BY JSON_VALUE(claim.value,'$.claimId') HAVING COUNT(*)>1)
    THROW 51009,'Credit successors require distinct identities, claim identities and references.',1;
  SET @ResolvedCredits=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),Input),N',') WITHIN GROUP(ORDER BY Id),N'')+N']' FROM @Sources);
END;
