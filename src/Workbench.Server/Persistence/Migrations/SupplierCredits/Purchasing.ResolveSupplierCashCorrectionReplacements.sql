-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ResolveSupplierCashCorrectionReplacements
  @TenantId uniqueidentifier,@Canonical nvarchar(max),@Sources nvarchar(max) OUTPUT,
  @Targets nvarchar(max) OUTPUT,@EvidenceBindings nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Cash replacement validation requires its owner transaction.',1;
  DECLARE @Policy nvarchar(max),@Scale int,@Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
    @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23);
  SELECT @Policy=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Version=@Config;
  SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Policy,'$.policies.scale'));
  IF @Policy IS NULL THROW 51009,'Accounting configuration changed.',1;
  IF @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR JSON_VALUE(@Policy,'$.policies.currency') COLLATE Latin1_General_100_BIN2<>@Currency
    THROW 51000,'Cash correction currency or policy is invalid.',1;
  DECLARE @Rows TABLE(Position int PRIMARY KEY,Input nvarchar(max));
  INSERT @Rows SELECT CONVERT(int,[key]),value FROM OPENJSON(@Sources);
  DECLARE @New TABLE(SourceId uniqueidentifier PRIMARY KEY,RevisionId uniqueidentifier UNIQUE);
  DECLARE @TargetRows TABLE(SourceId uniqueidentifier,ItemId uniqueidentifier,Amount decimal(28,4),PostingDate date,Version varchar(18),
    AccountId uniqueidentifier,AccountVersion uniqueidentifier,ControlHash varchar(64),PRIMARY KEY(SourceId,ItemId));
  DECLARE @EvidenceRows TABLE(SourceId uniqueidentifier PRIMARY KEY,Hash varchar(64));
  DECLARE @Position int,@Input nvarchar(max),@Replacement nvarchar(max),@Kind varchar(32),@Operation nvarchar(40),
    @Id uniqueidentifier,@Revision uniqueidentifier,@Posting date,@Amount decimal(28,4),@Funding uniqueidentifier,@FundingVersion uniqueidentifier,
    @Documents nvarchar(max),@Evidence nvarchar(max),@FundingPurpose nvarchar(40);
  DECLARE sources CURSOR LOCAL FAST_FORWARD FOR SELECT Position,Input FROM @Rows WHERE JSON_QUERY(Input,'$.replacement') IS NOT NULL ORDER BY Position;
  OPEN sources; FETCH NEXT FROM sources INTO @Position,@Input;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @Kind=JSON_VALUE(@Input,'$.sourceKind');
    SET @Operation=CASE @Kind WHEN 'SupplierRefund' THEN N'RecordSupplierRefund' WHEN 'SupplierRefundRepayment' THEN N'RecordSupplierRefundRepayment' END;
    IF @Operation IS NULL THROW 51004,'An unsupported cash family cannot create a successor.',1;
    SET @Replacement=JSON_QUERY(@Input,'$.replacement');
    EXEC Purchasing.CanonicalizeSupplierCash @Operation,@Replacement,@Replacement OUTPUT;
    SET @Id=CONVERT(uniqueidentifier,JSON_VALUE(@Replacement,'$.cashSourceId'));
    SET @Revision=CONVERT(uniqueidentifier,JSON_VALUE(@Replacement,'$.cashSourceRevisionId'));
    SET @Posting=CONVERT(date,JSON_VALUE(@Replacement,'$.postingDate'),23);
    SET @Amount=CONVERT(decimal(28,4),JSON_VALUE(@Replacement,'$.amount'));
    SET @Funding=CONVERT(uniqueidentifier,JSON_VALUE(@Replacement,'$.fundingAccountId'));
    SET @FundingVersion=CONVERT(uniqueidentifier,JSON_VALUE(@Replacement,'$.expectedFundingAccountVersion'));
    SET @FundingPurpose=NULL;
    IF JSON_VALUE(@Replacement,'$.expectedConfigurationVersion')<>JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')
      OR JSON_VALUE(@Replacement,'$.expectedPurchaseOrderVersion')<>JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion')
      OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Replacement,'$.purchaseOrderId'))<>@Po
      OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Replacement,'$.supplierId'))<>@Supplier
      OR JSON_VALUE(@Replacement,'$.currency') COLLATE Latin1_General_100_BIN2<>@Currency
      OR @Posting<>@Date OR CONVERT(date,JSON_VALUE(@Replacement,'$.actualDate'),23)>@Posting
      OR CONVERT(date,JSON_VALUE(@Replacement,'$.effectiveDate'),23)>@Posting
      THROW 51004,'A cash successor must preserve scope and use valid correction dates and versions.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierCashSources WHERE TenantId=@TenantId AND (Id=@Id OR RevisionId=@Revision))
      OR EXISTS(SELECT 1 FROM @New WHERE SourceId=@Id OR RevisionId=@Revision)
      THROW 51009,'A cash successor identity or revision is already used.',1;
    IF @Amount<>ROUND(@Amount,@Scale,1) OR EXISTS(SELECT 1 FROM OPENJSON(@Replacement,'$.settlements')
      WHERE CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount'))<>ROUND(CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),@Scale,1))
      OR (SELECT SUM(CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount'))) FROM OPENJSON(@Replacement,'$.settlements'))<>@Amount
      THROW 51000,'A cash successor requires exact gross settlement amounts and policy precision.',1;
    SELECT @FundingPurpose=Purpose FROM Accounting.Accounts WITH(UPDLOCK,HOLDLOCK)
      WHERE TenantId=@TenantId AND Id=@Funding AND Version=@FundingVersion AND Type='Asset' AND ArchivedAtUtc IS NULL AND Purpose IN('Bank','Cash');
    IF @FundingPurpose IS NULL THROW 51009,'The active successor Bank/Cash account changed or is unavailable.',1;
    IF JSON_VALUE(@Replacement,'$.method') COLLATE Latin1_General_100_BIN2<>@FundingPurpose
      THROW 51004,'The successor method must match its actual funding account.',1;
    INSERT @TargetRows SELECT @Id,item.Id,CONVERT(decimal(28,4),JSON_VALUE(target.value,'$.amount')),@Posting,
      CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),control.AccountId,control.AccountVersion,
      CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),(SELECT control.AccountId accountId,control.AccountVersion accountVersion,
        control.AccountCode accountCode,control.AccountName accountName,control.AccountType accountType,control.AccountPurpose accountPurpose
        FOR JSON PATH,WITHOUT_ARRAY_WRAPPER))),2)
      FROM OPENJSON(@Replacement,'$.settlements') target
      JOIN Purchasing.SupplierOpenItems item ON item.TenantId=@TenantId AND item.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.itemId'))
        AND item.SupplierId=@Supplier AND item.PurchaseOrderId=@Po AND item.Currency=@Currency AND item.SourcePostingDate<=@Posting
        AND ((@Kind='SupplierRefund' AND item.Kind IN('Advance','CreditReceivable')) OR (@Kind='SupplierRefundRepayment' AND item.Kind='RefundClearing'))
      JOIN Purchasing.SupplierItemVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=item.TenantId AND version.ItemId=item.Id
        AND version.RowVersion=TRY_CONVERT(binary(8),JSON_VALUE(target.value,'$.expectedItemVersion'),1)
      CROSS APPLY Purchasing.SupplierCashTargetControl(@TenantId,@Kind,item.Id) control;
    IF (SELECT COUNT(*) FROM @TargetRows WHERE SourceId=@Id)<>(SELECT COUNT(*) FROM OPENJSON(@Replacement,'$.settlements'))
      THROW 51009,'Every successor settlement requires its exact current genuine item and historical control.',1;
    SET @Evidence=JSON_QUERY(@Replacement,'$.evidence');
    EXEC Purchasing.ValidateBillEvidence @TenantId,@Po,@Evidence,@Documents OUTPUT;
    INSERT @EvidenceRows VALUES(@Id,CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Documents)),2));
    INSERT @New VALUES(@Id,@Revision);
    SET @Input=JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(@Input,'$.replacement',JSON_QUERY(@Replacement)),
      '$.replacementId',CONVERT(nvarchar(36),@Id)),'$.replacementRevisionId',CONVERT(nvarchar(36),@Revision)),
      '$.replacementAmount',CONVERT(nvarchar(60),@Amount));
    UPDATE @Rows SET Input=@Input WHERE Position=@Position;
    FETCH NEXT FROM sources INTO @Position,@Input;
  END;
  CLOSE sources; DEALLOCATE sources;
  SET @Sources=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),Input),N',') WITHIN GROUP(ORDER BY Position),N'')+N']' FROM @Rows);
  SET @Targets=(SELECT SourceId sourceId,ItemId itemId,CONVERT(nvarchar(60),Amount) amount,PostingDate postingDate,Version version,
    AccountId accountId,AccountVersion accountVersion,ControlHash controlSha256 FROM @TargetRows ORDER BY SourceId,ItemId FOR JSON PATH);
  SET @EvidenceBindings=(SELECT SourceId sourceId,Hash documentsSha256 FROM @EvidenceRows ORDER BY SourceId FOR JSON PATH);
END;
