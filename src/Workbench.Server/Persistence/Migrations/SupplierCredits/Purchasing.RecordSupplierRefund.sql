-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.RecordSupplierRefund
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRY
    BEGIN TRAN;
    DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Resource nvarchar(255),
      @Lock int,@Canonical nvarchar(max),@Result nvarchar(max);
    IF @Tenant IS NULL THROW 51003,'Current refund authority is required.',1;
    SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
    EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @Lock<0 THROW 51009,'Accounting is being changed. Retry.',1;
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierRefundsRecord'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current refund authority is required.',1; THROW; END CATCH;
    IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid refund request identity.',1;
    EXEC Purchasing.CanonicalizeSupplierCash N'RecordSupplierRefund',@Command,@Canonical OUTPUT;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId)
    BEGIN
      IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId
        AND Operation='RecordSupplierRefund' AND ActorId=@ActorId AND JSON_VALUE(ResultJson,'$.state') IS NULL
        AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
        THROW 51009,'Refund request identity has different or pending content.',1;
      SELECT @Result=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId;
      COMMIT; SELECT @Result ResultJson; RETURN;
    END;
    DECLARE @Source uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.cashSourceId')),
      @Revision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.cashSourceRevisionId')),
      @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
      @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
      @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
      @Funding uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.fundingAccountId')),
      @FundingVersion uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedFundingAccountVersion')),
      @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Method varchar(12)=JSON_VALUE(@Canonical,'$.method'),
      @Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),@Actual date=CONVERT(date,JSON_VALUE(@Canonical,'$.actualDate'),23),
      @Effective date=CONVERT(date,JSON_VALUE(@Canonical,'$.effectiveDate'),23),@Amount decimal(28,4)=CONVERT(decimal(28,4),JSON_VALUE(@Canonical,'$.amount')),
      @Group uniqueidentifier=NEWID(),@Now datetimeoffset=SYSUTCDATETIME(),@Policy nvarchar(max),@Scale int,@Purpose nvarchar(40),
      @Reference nvarchar(200)=JSON_VALUE(@Canonical,'$.reference'),@Notes nvarchar(2000)=JSON_VALUE(@Canonical,'$.notes');
    IF @Actual>@Date OR @Effective>@Date THROW 51000,'Refund dates are invalid.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierCashSources WHERE TenantId=@Tenant AND (Id=@Source OR RevisionId=@Revision))
      THROW 51009,'Supplier cash source already exists.',1;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND Id=@Po AND State='Ordered'
      AND IsDeleted=0 AND SupplierId=@Supplier AND Currency=@Currency AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
      THROW 51009,'Purchase revision changed.',1;
    SELECT @Policy=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND Version=@Config;
    IF @Policy IS NULL THROW 51009,'Accounting configuration changed.',1;
    SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Policy,'$.policies.scale'));
    IF @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR JSON_VALUE(@Policy,'$.policies.currency') COLLATE Latin1_General_100_BIN2<>@Currency
      OR @Amount<>ROUND(@Amount,@Scale,1) OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.settlements') WHERE
        CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount'))<>ROUND(CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),@Scale,1))
      THROW 51000,'Refund currency or precision is invalid.',1;
    IF (SELECT SUM(CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount'))) FROM OPENJSON(@Canonical,'$.settlements'))<>@Amount
      THROW 51000,'Refund amount must exactly equal its settlements.',1;
    SELECT @Purpose=Purpose FROM Accounting.Accounts WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND Id=@Funding
      AND Type='Asset' AND Purpose COLLATE Latin1_General_100_BIN2 IN('Bank','Cash') AND ArchivedAtUtc IS NULL;
    IF @Purpose IS NULL THROW 51004,'Refund funding must be active Bank or Cash.',1;
    IF @Method COLLATE Latin1_General_100_BIN2<>@Purpose THROW 51000,'Refund method does not match the actual funding account.',1;
    IF NOT EXISTS(SELECT 1 FROM Accounting.Accounts WHERE TenantId=@Tenant AND Id=@Funding AND Version=@FundingVersion)
      THROW 51009,'Funding account revision changed.',1;
    EXEC Accounting.EnsureOpenPeriod @PostingDate=@Date,@ExpectedConfigurationVersion=@Config;
    DECLARE @Targets TABLE(Position int PRIMARY KEY,Id uniqueidentifier,MovementId uniqueidentifier,ItemId uniqueidentifier,
      Amount decimal(28,4),AccountId uniqueidentifier,AccountVersion uniqueidentifier,JournalOrdinal int);
    INSERT @Targets(Position,Id,MovementId,ItemId,Amount)
      SELECT CONVERT(int,[key]),NEWID(),NEWID(),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.itemId')),
        CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@Canonical,'$.settlements');
    IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.settlements') target WHERE NOT EXISTS(
      SELECT 1 FROM Purchasing.SupplierItemVersions version WITH(UPDLOCK,HOLDLOCK) WHERE version.TenantId=@Tenant
        AND version.ItemId=CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.itemId'))
        AND version.RowVersion=CONVERT(binary(8),JSON_VALUE(target.value,'$.expectedItemVersion'),1)))
      THROW 51009,'Supplier asset revision changed.',1;
    IF EXISTS(SELECT 1 FROM @Targets target WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems item
      WHERE item.TenantId=@Tenant AND item.Id=target.ItemId AND item.Kind IN('Advance','CreditReceivable') AND item.SupplierId=@Supplier
        AND item.PurchaseOrderId=@Po AND item.Currency=@Currency AND item.SourcePostingDate<=@Date)
      OR (SELECT COUNT(*) FROM Purchasing.SupplierAssetControl(@Tenant,target.ItemId))<>1)
      THROW 51004,'Authentic refundable supplier assets unavailable on this posting date.',1;
    UPDATE target SET AccountId=control.AccountId,AccountVersion=control.AccountVersion FROM @Targets target
      CROSS APPLY Purchasing.SupplierAssetControl(@Tenant,target.ItemId) control;
    ;WITH accounts AS(SELECT AccountId,AccountVersion,1+ROW_NUMBER() OVER(ORDER BY AccountId,AccountVersion) ordinal
      FROM @Targets GROUP BY AccountId,AccountVersion)
    UPDATE target SET JournalOrdinal=accounts.ordinal FROM @Targets target JOIN accounts
      ON accounts.AccountId=target.AccountId AND accounts.AccountVersion=target.AccountVersion;
    DECLARE @Documents nvarchar(max),@Evidence nvarchar(max)=JSON_QUERY(@Canonical,'$.evidence');
    EXEC Purchasing.ValidateBillEvidence @Tenant,@Po,@Evidence,@Documents OUTPUT;
    DECLARE @Snapshot nvarchar(max)=(SELECT 1 schemaVersion,@Group groupId,JSON_QUERY(@Canonical) command,
      JSON_QUERY((SELECT Id id,Name name FROM Purchasing.Suppliers WHERE TenantId=@Tenant AND Id=@Supplier FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) supplier,
      JSON_QUERY((SELECT Id id,Revision revision,OrderDate orderDate,ContentJson content FROM Purchasing.DraftOrders WHERE TenantId=@Tenant AND Id=@Po FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) purchase,
      JSON_QUERY((SELECT Id settlementId,MovementId movementId,ItemId itemId,CONVERT(nvarchar(60),Amount) amount,
        AccountId accountId,AccountVersion accountVersion,JournalOrdinal journalOrdinal FROM @Targets ORDER BY Position FOR JSON PATH)) settlements,
      JSON_QUERY((SELECT JSON_QUERY(@Documents) documents,JSON_VALUE(@Evidence,'$.missingEvidenceReason') missingEvidenceReason
        FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) evidence FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    IF DATALENGTH(@Snapshot)>262144 THROW 51000,'Refund source snapshot exceeds its bound.',1;
    INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc) VALUES(@Tenant,@Group,'RecordRefund',@Source,@Now);
    INSERT Purchasing.SupplierFinancialReceipts(TenantId,RequestId,GroupId,Operation,ActorId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
      VALUES(@Tenant,@RequestId,@Group,'RecordSupplierRefund',@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),N'{"state":"pending"}',@Now);
    INSERT Purchasing.SupplierCashSources(TenantId,Id,RevisionId,Kind,SupplierId,PurchaseOrderId,Currency,ActualDate,EffectiveDate,PostingDate,
      Amount,Method,FundingAccountId,FundingAccountVersion,FundingAccountPurpose,Reference,Notes,SourceSnapshotJson,ActorId,GroupId,RecordedAtUtc)
      VALUES(@Tenant,@Source,@Revision,'SupplierRefund',@Supplier,@Po,@Currency,@Actual,@Effective,@Date,@Amount,@Method,
        @Funding,@FundingVersion,@Purpose,@Reference,@Notes,@Snapshot,@ActorId,@Group,@Now);
    DECLARE @Lines nvarchar(max)=(SELECT ordinal,accountId,accountVersion,CONVERT(nvarchar(60),debit) debit,CONVERT(nvarchar(60),credit) credit FROM(
      SELECT 1 ordinal,@Funding accountId,@FundingVersion accountVersion,@Amount debit,CONVERT(decimal(28,4),0) credit
      UNION ALL SELECT JournalOrdinal,AccountId,AccountVersion,0,CONVERT(decimal(28,4),SUM(Amount)) FROM @Targets
        GROUP BY JournalOrdinal,AccountId,AccountVersion) lines ORDER BY ordinal FOR JSON PATH);
    DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
    INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@RequestId,N'SupplierRefundsRecord',N'Supplier.Refund',1,
      @Canonical,N'SupplierRefund',@Source,@Revision,N'Refund',1,@Config,@Currency,@Actual,@Effective,@Date,@Reference,@Notes,@Snapshot,@Lines;
    UPDATE sourceEvent SET RecordedAtUtc=@Now FROM Accounting.SourceEvents sourceEvent JOIN @Posted posted ON posted.SourceEventId=sourceEvent.Id WHERE sourceEvent.TenantId=@Tenant;
    UPDATE journal SET RecordedAtUtc=@Now FROM Accounting.JournalEntries journal JOIN @Posted posted ON posted.JournalId=journal.Id WHERE journal.TenantId=@Tenant;
    UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND RequestId=@RequestId;
    UPDATE audit SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents audit JOIN @Posted posted ON posted.JournalId=audit.TargetId
      WHERE audit.TenantId=@Tenant AND audit.Action='Accounting.PostJournal';
    DECLARE @Journal uniqueidentifier=(SELECT JournalId FROM @Posted),@EvidenceSet uniqueidentifier;
    EXEC Purchasing.AppendSupplierCashSettlement @Tenant,@Source,@Journal;
    EXEC Accounting.CaptureFinancialEvidence N'SupplierRefund',@Source,@Revision,@EvidenceSet OUTPUT;
    SET @Result=(SELECT @Group groupId,@Source sourceId,@Revision sourceRevisionId,
      CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1) version,@Now recordedAtUtc,
      JSON_QUERY(N'["'+CONVERT(nvarchar(36),@Journal)+N'"]') journalIds,JSON_QUERY(N'[]') itemIds,JSON_QUERY(N'[]') applicationIds,
      JSON_QUERY((SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N',') WITHIN GROUP(ORDER BY Position)+N']' FROM @Targets)) settlementIds
      FROM Purchasing.SupplierCashVersions version WHERE version.TenantId=@Tenant AND version.CashSourceId=@Source FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=@Result WHERE TenantId=@Tenant AND RequestId=@RequestId AND JSON_VALUE(ResultJson,'$.state')='pending';
    IF @@ROWCOUNT<>1 THROW 51009,'Refund receipt was already finalized.',1;
    COMMIT; SELECT @Result ResultJson;
  END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
