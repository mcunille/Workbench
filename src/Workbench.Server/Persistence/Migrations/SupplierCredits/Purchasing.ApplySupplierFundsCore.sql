-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Purchasing.ApplySupplierFundsCore
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max),@OwnerRequestId uniqueidentifier=NULL
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRY
    BEGIN TRAN;
    DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@LockResult int,
      @Resource nvarchar(255),@Canonical nvarchar(max),@Result nvarchar(max);
    IF @TenantId IS NULL THROW 51003,'Current supplier allocation authority is required.',1;
    SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@TenantId);
    EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier allocation authority is required.',1; THROW; END CATCH;
    IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid request identity.',1;
    EXEC Purchasing.ValidateSupplierFinancialCommand N'ApplySupplierFunds',@Command,@Canonical OUTPUT;
    IF (SELECT COUNT(*) FROM OPENJSON(@Canonical))<>11
      OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN
        ('schemaVersion','operation','expectedConfigurationVersion','purchaseOrderId','expectedPurchaseOrderVersion',
         'supplierId','currency','postingDate','fundingItemId','expectedFundingItemVersion','targets')
        OR ([key]='schemaVersion' AND type<>2) OR ([key]='targets' AND type<>4)
        OR ([key] NOT IN('schemaVersion','targets') AND type<>1))
      OR NOT EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.targets'))
      OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.targets') e WHERE e.type<>5
        OR (SELECT COUNT(*) FROM OPENJSON(e.value))<>4
        OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('billId','itemId','expectedItemVersion','amount') OR p.type<>1))
      THROW 51000,'Invalid supplier application fields.',1;
    IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('expectedConfigurationVersion','purchaseOrderId','supplierId','fundingItemId')
        AND (DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL OR TRY_CONVERT(uniqueidentifier,value)='00000000-0000-0000-0000-000000000000'))
      OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical) WHERE [key] IN('expectedPurchaseOrderVersion','expectedFundingItemVersion')
        AND (DATALENGTH(value)<>36 OR TRY_CONVERT(binary(8),value,1) IS NULL))
      OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.targets') e CROSS APPLY OPENJSON(e.value) p
        WHERE (p.[key]='billId' AND (DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL OR TRY_CONVERT(uniqueidentifier,p.value)='00000000-0000-0000-0000-000000000000'))
          OR (p.[key]='expectedItemVersion' AND (DATALENGTH(p.value)<>36 OR TRY_CONVERT(binary(8),p.value,1) IS NULL))
          OR (p.[key]='amount' AND TRY_CONVERT(decimal(28,4),p.value)<=0))
      OR DATALENGTH(JSON_VALUE(@Canonical,'$.postingDate'))<>20 OR TRY_CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23) IS NULL
      OR DATALENGTH(JSON_VALUE(@Canonical,'$.currency'))<>6
      THROW 51000,'Invalid supplier application values.',1;
    DECLARE @CompositionGroup uniqueidentifier,@CompositionInstant datetimeoffset;
    IF @OwnerRequestId IS NOT NULL
      EXEC Purchasing.RequireSupplierComposition @ActorId,@OwnerRequestId,@RequestId,N'ApplySupplierFunds',@Canonical,@CompositionGroup OUTPUT,@CompositionInstant OUTPUT;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
    BEGIN
      IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
        AND Operation='ApplySupplierFunds' AND ActorId=@ActorId AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
        THROW 51009,'Request identity has different content.',1;
      SELECT @Result=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
      COMMIT; IF @OwnerRequestId IS NULL SELECT @Result ResultJson; RETURN;
    END;
    DECLARE @PoId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
      @SupplierId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
      @FundingId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.fundingItemId')),
      @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
      @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),
      @Group uniqueidentifier=COALESCE(@CompositionGroup,NEWID()),@Now datetimeoffset=COALESCE(@CompositionInstant,SYSUTCDATETIME());
    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@PoId AND State='Ordered'
      AND IsDeleted=0 AND SupplierId=@SupplierId AND Currency=@Currency AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
      THROW 51009,'Purchase revision changed.',1;
    DECLARE @Targets TABLE(Ordinal int PRIMARY KEY,ApplicationId uniqueidentifier,ItemId uniqueidentifier,BillId uniqueidentifier,
      Version binary(8),Amount decimal(28,4));
    INSERT @Targets SELECT CONVERT(int,[key]),NEWID(),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.itemId')),
      CONVERT(uniqueidentifier,JSON_VALUE(value,'$.billId')),CONVERT(binary(8),JSON_VALUE(value,'$.expectedItemVersion'),1),
      CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@Canonical,'$.targets');
    DECLARE @Items TABLE(Id uniqueidentifier PRIMARY KEY,Version binary(8));
    INSERT @Items SELECT ItemId,Version FROM @Targets;
    IF EXISTS(SELECT 1 FROM @Items WHERE Id=@FundingId) THROW 51004,'Funding and debt must differ.',1;
    INSERT @Items VALUES(@FundingId,CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedFundingItemVersion'),1));
    DECLARE @Locked int;
    SELECT @Locked=COUNT(*) FROM Purchasing.SupplierItemVersions v WITH(UPDLOCK,HOLDLOCK)
      JOIN @Items i ON i.Id=v.ItemId AND i.Version=v.RowVersion WHERE v.TenantId=@TenantId;
    IF @Locked<>(SELECT COUNT(*) FROM @Items) THROW 51009,'Supplier item revision changed.',1;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems WHERE TenantId=@TenantId AND Id=@FundingId AND Kind IN('Advance','CreditReceivable')
        AND SupplierId=@SupplierId AND PurchaseOrderId=@PoId AND Currency=@Currency AND SourcePostingDate<=@Date)
      OR EXISTS(SELECT 1 FROM @Targets t WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems i
        JOIN Purchasing.SupplierBillPostings b ON b.TenantId=i.TenantId AND b.BillId=i.BillId AND b.RevisionId=i.SourceRevisionId
        WHERE i.TenantId=@TenantId AND i.Id=t.ItemId AND i.Kind='Payable' AND i.BillId=t.BillId AND i.SourceKind='SupplierBill'
          AND i.SourceId=i.BillId AND i.SupplierId=@SupplierId AND i.PurchaseOrderId=@PoId AND i.Currency=@Currency AND i.SourcePostingDate<=@Date))
      THROW 51004,'Posted supplier funding or bill is unavailable on the application date.',1;
    IF EXISTS(SELECT 1 FROM @Items i WHERE (SELECT COUNT(*) FROM Purchasing.SupplierItemControl(@TenantId,i.Id))<>1)
      THROW 51004,'Historical supplier control evidence is unavailable.',1;
    IF TRY_CONVERT(decimal(28,4),(SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Targets)) IS NULL
      THROW 51000,'Supplier application total exceeds supported money.',1;
    DECLARE @Movements nvarchar(max)=(SELECT itemId,@Group groupId,@Date postingDate,CONVERT(nvarchar(60),-Amount) amount
      FROM(SELECT ItemId itemId,Amount FROM @Targets UNION ALL SELECT @FundingId,Amount FROM @Targets) m FOR JSON PATH);
    EXEC Purchasing.AssertSupplierAvailability @TenantId,@Movements;
    DECLARE @Application uniqueidentifier,@Debt uniqueidentifier,@Amount decimal(28,4),@Lines nvarchar(max),@Snapshot nvarchar(max),
      @ControlItems nvarchar(max),@KernelRequest uniqueidentifier;
    DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
    DECLARE @Events TABLE(ApplicationId uniqueidentifier,JournalId uniqueidentifier);
    DECLARE applications CURSOR LOCAL FAST_FORWARD FOR SELECT ApplicationId,ItemId,Amount FROM @Targets ORDER BY Ordinal;
    OPEN applications; FETCH NEXT FROM applications INTO @Application,@Debt,@Amount;
    WHILE @@FETCH_STATUS=0
    BEGIN
      SET @Snapshot=(SELECT @Application applicationId,@Group groupId,@FundingId fundingItemId,@Debt debtItemId,
        @OwnerRequestId ownerRequestId,CASE WHEN @OwnerRequestId IS NOT NULL THEN @RequestId END ownerCommandRequestId,
        CONVERT(nvarchar(60),@Amount) amount FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
      SET @ControlItems=(SELECT ordinal,itemId FROM(VALUES(1,@Debt),(2,@FundingId)) x(ordinal,itemId) FOR JSON PATH);
      SET @Lines=(SELECT ordinal,c.AccountId accountId,c.AccountVersion accountVersion,
        CONVERT(nvarchar(60),CASE WHEN ordinal=1 THEN @Amount ELSE 0 END) debit,
        CONVERT(nvarchar(60),CASE WHEN ordinal=2 THEN @Amount ELSE 0 END) credit
        FROM(VALUES(1,@Debt),(2,@FundingId)) x(ordinal,itemId) CROSS APPLY Purchasing.SupplierItemControl(@TenantId,itemId) c ORDER BY ordinal FOR JSON PATH);
      SET @KernelRequest=NEWID(); DELETE @Posted;
      INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@KernelRequest,N'SupplierAllocationsManage',N'Supplier.Apply',1,
        @Canonical,N'SupplierApplication',@Application,@Group,N'Apply',1,@Config,@Currency,@Date,@Date,@Date,NULL,NULL,@Snapshot,@Lines,@ControlItems;
      -- Match the existing recognition owner's atomic recorded instant convention.
      UPDATE s SET RecordedAtUtc=@Now FROM Accounting.SourceEvents s JOIN @Posted p ON p.SourceEventId=s.Id WHERE s.TenantId=@TenantId;
      UPDATE j SET RecordedAtUtc=@Now FROM Accounting.JournalEntries j JOIN @Posted p ON p.JournalId=j.Id WHERE j.TenantId=@TenantId;
      UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND RequestId=@KernelRequest;
      UPDATE a SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents a JOIN @Posted p ON p.JournalId=a.TargetId
        WHERE a.TenantId=@TenantId AND a.Action='Accounting.PostJournal';
      INSERT @Events SELECT @Application,JournalId FROM @Posted;
      FETCH NEXT FROM applications INTO @Application,@Debt,@Amount;
    END;
    CLOSE applications; DEALLOCATE applications;
    DECLARE @EventJson nvarchar(max)=(SELECT ApplicationId applicationId,JournalId journalId FROM @Events FOR JSON PATH);
    EXEC Purchasing.AppendSupplierEventGroup @TenantId,@Group,@Now,@EventJson;
    SET @Result=(SELECT @Group groupId,@Now recordedAtUtc,
      JSON_QUERY((SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),ApplicationId)+N'"'),N',') WITHIN GROUP(ORDER BY Ordinal)+N']' FROM @Targets)) applicationIds,
      JSON_QUERY((SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),e.JournalId)+N'"'),N',') WITHIN GROUP(ORDER BY t.Ordinal)+N']' FROM @Events e JOIN @Targets t ON t.ApplicationId=e.ApplicationId)) journalIds,
      JSON_QUERY((SELECT v.ItemId itemId,CONVERT(varchar(18),CONVERT(binary(8),v.RowVersion),1) version FROM Purchasing.SupplierItemVersions v
        JOIN @Items i ON i.Id=v.ItemId WHERE v.TenantId=@TenantId ORDER BY v.ItemId FOR JSON PATH)) itemVersions
      FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    INSERT Purchasing.SupplierFinancialReceipts(TenantId,RequestId,GroupId,Operation,ActorId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
      VALUES(@TenantId,@RequestId,@Group,'ApplySupplierFunds',@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);
    COMMIT; IF @OwnerRequestId IS NULL SELECT @Result ResultJson;
  END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
