-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Purchasing.RecordSupplierPaymentCore
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max),@OwnerRequestId uniqueidentifier=NULL
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRY
    BEGIN TRAN;
    DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@LockResult int,
      @Resource nvarchar(255),@Canonical nvarchar(max),@Result nvarchar(max);
    IF @TenantId IS NULL THROW 51003,'Current supplier payment authority is required.',1;
    SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@TenantId);
    EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
    DECLARE @PaymentPermission nvarchar(40)=CASE WHEN @OwnerRequestId IS NULL THEN N'SupplierPaymentsRecord' ELSE N'SupplierPaymentsCorrect' END;
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,@PaymentPermission; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier payment authority is required.',1; THROW; END CATCH;
    IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid request identity.',1;
    EXEC Purchasing.ValidateSupplierFinancialCommand N'RecordSupplierPayment',@Command,@Canonical OUTPUT;
    DECLARE @PaymentInput nvarchar(max)=@Canonical;    IF (SELECT COUNT(*) FROM OPENJSON(@PaymentInput))<>20
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN
        ('schemaVersion','operation','expectedConfigurationVersion','purchaseOrderId','expectedPurchaseOrderVersion','supplierId','currency',
         'postingDate','paymentId','paymentRevisionId','paymentDate','effectiveDate','amount','method','fundingAccountId',
         'expectedFundingAccountVersion','reference','notes','evidence','allocations')
        OR ([key]='schemaVersion' AND type<>2) OR ([key]='allocations' AND type<>4) OR ([key]='evidence' AND type<>5)
        OR ([key] IN('reference','notes') AND type NOT IN(0,1))
        OR ([key] NOT IN('schemaVersion','allocations','evidence','reference','notes') AND type<>1))
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput,'$.allocations') e WHERE e.type<>5
        OR (SELECT COUNT(*) FROM OPENJSON(e.value))<>4
        OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('billId','itemId','expectedItemVersion','amount') OR p.type<>1))
      THROW 51000,'Invalid supplier payment fields.',1;
    IF EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE [key] IN('expectedConfigurationVersion','purchaseOrderId','supplierId','paymentId','paymentRevisionId','fundingAccountId','expectedFundingAccountVersion')
        AND (DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL OR TRY_CONVERT(uniqueidentifier,value)='00000000-0000-0000-0000-000000000000'))
      OR DATALENGTH(JSON_VALUE(@PaymentInput,'$.expectedPurchaseOrderVersion'))<>36
      OR TRY_CONVERT(binary(8),JSON_VALUE(@PaymentInput,'$.expectedPurchaseOrderVersion'),1) IS NULL
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput,'$.allocations') e CROSS APPLY OPENJSON(e.value) p WHERE
        (p.[key]='billId' AND (DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL OR TRY_CONVERT(uniqueidentifier,p.value)='00000000-0000-0000-0000-000000000000'))
        OR (p.[key]='expectedItemVersion' AND (DATALENGTH(p.value)<>36 OR TRY_CONVERT(binary(8),p.value,1) IS NULL))
        OR (p.[key]='amount' AND TRY_CONVERT(decimal(28,4),p.value)<=0))
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE [key] IN('postingDate','paymentDate','effectiveDate')
        AND (DATALENGTH(value)<>20 OR TRY_CONVERT(date,value,23) IS NULL))
      OR DATALENGTH(JSON_VALUE(@PaymentInput,'$.currency'))<>6
      OR TRY_CONVERT(decimal(28,4),JSON_VALUE(@PaymentInput,'$.amount'))<=0
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE [key]='method' AND value COLLATE Latin1_General_100_BIN2 NOT IN('Bank','Cash'))
      OR EXISTS(SELECT 1 FROM OPENJSON(@PaymentInput) WHERE ([key]='reference' AND DATALENGTH(value)>400) OR ([key]='notes' AND DATALENGTH(value)>4000))
      THROW 51000,'Invalid supplier payment values.',1;    DECLARE @EvidenceInput nvarchar(max)=JSON_QUERY(@Canonical,'$.evidence');    IF (SELECT COUNT(*) FROM OPENJSON(@EvidenceInput))<>2
      OR EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN('documents','missingEvidenceReason')
        OR ([key]='documents' AND type<>4) OR ([key]='missingEvidenceReason' AND type NOT IN(0,1)))
      OR EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput) WHERE [key]='missingEvidenceReason' AND DATALENGTH(value)>4000)
      OR (NOT EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput,'$.documents')) AND NULLIF(TRIM(JSON_VALUE(@EvidenceInput,'$.missingEvidenceReason')),'') IS NULL)
      OR EXISTS(SELECT 1 FROM OPENJSON(@EvidenceInput,'$.documents') d WHERE d.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(d.value))<>2
        OR EXISTS(SELECT 1 FROM OPENJSON(d.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('documentId','revisionId')
          OR p.type<>1 OR DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL OR TRY_CONVERT(uniqueidentifier,p.value)='00000000-0000-0000-0000-000000000000'))
      THROW 51000,'Invalid supplier payment evidence.',1;    IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.allocations'))
    BEGIN
      BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
      BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier allocation authority is required.',1; THROW; END CATCH;
    END;
    DECLARE @CompositionGroup uniqueidentifier,@CompositionInstant datetimeoffset;
    IF @OwnerRequestId IS NOT NULL
      EXEC Purchasing.RequireSupplierComposition @ActorId,@OwnerRequestId,@RequestId,N'RecordSupplierPayment',@Canonical,@CompositionGroup OUTPUT,@CompositionInstant OUTPUT;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
    BEGIN
      IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
        AND Operation='RecordSupplierPayment' AND ActorId=@ActorId AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
        THROW 51009,'Request identity has different content.',1;
      SELECT @Result=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
      COMMIT; IF @OwnerRequestId IS NULL SELECT @Result ResultJson; RETURN;
    END;
    DECLARE @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
      @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
      @Payment uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.paymentId')),
      @Revision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.paymentRevisionId')),
      @Funding uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.fundingAccountId')),
      @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
      @Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),@PaymentDate date=CONVERT(date,JSON_VALUE(@Canonical,'$.paymentDate'),23),
      @Effective date=CONVERT(date,JSON_VALUE(@Canonical,'$.effectiveDate'),23),@Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),
      @Amount decimal(28,4)=CONVERT(decimal(28,4),JSON_VALUE(@Canonical,'$.amount')),@Group uniqueidentifier=COALESCE(@CompositionGroup,NEWID()),
      @Now datetimeoffset=COALESCE(@CompositionInstant,SYSUTCDATETIME()),@Payload nvarchar(max),@Scale int,@Advance uniqueidentifier;
    IF @PaymentDate>@Date OR @Effective>@Date OR @PaymentDate>CONVERT(date,@Now)
      THROW 51000,'Supplier payment dates are invalid.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierPayments WHERE TenantId=@TenantId AND (Id=@Payment OR RevisionId=@Revision))
      THROW 51009,'Supplier payment source already exists.',1;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Po AND State='Ordered' AND IsDeleted=0
        AND SupplierId=@Supplier AND Currency=@Currency AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
      THROW 51009,'Purchase revision changed.',1;
    SELECT @Payload=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Version=@Config;
    IF @Payload IS NULL THROW 51009,'Accounting configuration changed.',1;
    SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Payload,'$.policies.scale'));
    SELECT @Advance=TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountId')) FROM OPENJSON(@Payload,'$.mappings') WHERE JSON_VALUE(value,'$.slot')='SupplierAdvance';
    IF @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR @Currency<>JSON_VALUE(@Payload,'$.policies.currency')
      OR @Amount<>ROUND(@Amount,@Scale,1) THROW 51000,'Payment currency or precision is invalid.',1;
    IF NOT EXISTS(SELECT 1 FROM Accounting.Accounts WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Funding AND ArchivedAtUtc IS NULL
        AND Type='Asset' AND Purpose COLLATE Latin1_General_100_BIN2 IN('Bank','Cash'))
      THROW 51004,'Payment funding account must be active Bank or Cash.',1;
    IF NOT EXISTS(SELECT 1 FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Funding
        AND Version=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedFundingAccountVersion')))
      THROW 51009,'Funding account revision changed.',1;
    IF (SELECT COUNT(*) FROM OPENJSON(@Payload,'$.mappings') WHERE JSON_VALUE(value,'$.slot')='SupplierAdvance')<>1
      OR NOT EXISTS(SELECT 1 FROM Accounting.Accounts WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Advance AND ArchivedAtUtc IS NULL AND Type='Asset' AND Purpose='SupplierAdvance')
      THROW 51004,'Current supplier advance account is unavailable.',1;
    DECLARE @Targets TABLE(Position int PRIMARY KEY,ApplicationId uniqueidentifier,ItemId uniqueidentifier,BillId uniqueidentifier,Amount decimal(28,4),
      AccountId uniqueidentifier,AccountVersion uniqueidentifier,Ordinal int);
    INSERT @Targets(Position,ApplicationId,ItemId,BillId,Amount)
      SELECT CONVERT(int,[key]),NEWID(),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.itemId')),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.billId')),
        CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@Canonical,'$.allocations');
    IF EXISTS(SELECT 1 FROM @Targets WHERE Amount<>ROUND(Amount,@Scale,1)) THROW 51000,'Allocation precision is invalid.',1;
    IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.allocations') t WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierItemVersions v WITH(UPDLOCK,HOLDLOCK)
      WHERE v.TenantId=@TenantId AND v.ItemId=CONVERT(uniqueidentifier,JSON_VALUE(t.value,'$.itemId')) AND v.RowVersion=CONVERT(binary(8),JSON_VALUE(t.value,'$.expectedItemVersion'),1)))
      THROW 51009,'Supplier item revision changed.',1;
    IF EXISTS(SELECT 1 FROM @Targets t WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems i
      JOIN Purchasing.SupplierBillPostings b ON b.TenantId=i.TenantId AND b.BillId=i.BillId AND b.RevisionId=i.SourceRevisionId
      WHERE i.TenantId=@TenantId AND i.Id=t.ItemId AND i.BillId=t.BillId AND i.SourceKind='SupplierBill' AND i.SourceId=i.BillId
        AND i.Kind='Payable' AND i.SupplierId=@Supplier AND i.PurchaseOrderId=@Po AND i.Currency=@Currency AND i.SourcePostingDate<=@Date))
      THROW 51004,'Posted supplier bill is unavailable on the payment date.',1;
    IF EXISTS(SELECT 1 FROM @Targets t WHERE (SELECT COUNT(*) FROM Purchasing.SupplierItemControl(@TenantId,t.ItemId))<>1)
      THROW 51004,'Historical supplier control evidence is unavailable.',1;
    UPDATE t SET AccountId=c.AccountId,AccountVersion=c.AccountVersion FROM @Targets t CROSS APPLY Purchasing.SupplierItemControl(@TenantId,t.ItemId) c;
    -- Different bills can own different immutable versions of one account.
    -- Preserve each historical snapshot in its own net debit line.
    DECLARE @Allocated decimal(38,4)=COALESCE((SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Targets),0);
    IF @Allocated>@Amount THROW 51009,'Payment allocations exceed gross funding.',1;
    DECLARE @Net decimal(28,4)=@Amount-@Allocated;
    DECLARE @Line TABLE(Ordinal int,AccountId uniqueidentifier,AccountVersion uniqueidentifier,Debit decimal(28,4),Credit decimal(28,4));
    INSERT @Line SELECT ROW_NUMBER() OVER(ORDER BY AccountId,AccountVersion),AccountId,AccountVersion,SUM(Amount),0 FROM @Targets GROUP BY AccountId,AccountVersion;
    UPDATE t SET Ordinal=l.Ordinal FROM @Targets t JOIN @Line l ON l.AccountId=t.AccountId AND l.AccountVersion=t.AccountVersion;
    IF @Net>0 INSERT @Line SELECT (SELECT COUNT(*)+1 FROM @Line),Id,Version,@Net,0 FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Advance;
    INSERT @Line SELECT (SELECT COUNT(*)+1 FROM @Line),Id,Version,0,@Amount FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Funding;
    DECLARE @Documents nvarchar(max);
    DECLARE @InheritedEvidence uniqueidentifier=(SELECT original.Id FROM Purchasing.SupplierPaymentCorrections c JOIN Accounting.FinancialEvidenceSets original
  ON original.TenantId=c.TenantId AND original.OwnerKind='SupplierPayment' AND original.OwnerId=c.OriginalPaymentId WHERE c.TenantId=@TenantId AND c.GroupId=@Group);
EXEC Accounting.ValidateInheritedFinancialEvidence @TenantId,@Po,@EvidenceInput,@InheritedEvidence,@Documents OUTPUT;
    DECLARE @Snapshot nvarchar(max)=(SELECT 1 schemaVersion,@Group groupId,@OwnerRequestId correctionRequestId,
      CASE WHEN @OwnerRequestId IS NOT NULL THEN @RequestId END correctionCommandRequestId,JSON_QUERY(@Canonical) command,
      JSON_QUERY((SELECT Id id,Version version,Code code,Name name,Type type,Purpose purpose FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Advance FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) advanceAccount,
      JSON_QUERY((SELECT Id id,Version version,Code code,Name name,Type type,Purpose purpose FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Funding FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) fundingAccount,
      JSON_QUERY((SELECT Id id,Name name FROM Purchasing.Suppliers WHERE TenantId=@TenantId AND Id=@Supplier FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) supplier,
      JSON_QUERY((SELECT Id id,Revision revision,OrderDate orderDate,ContentJson content FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@Po FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) purchaseOrder,
      JSON_QUERY((SELECT JSON_QUERY(@Documents) documents,JSON_VALUE(@EvidenceInput,'$.missingEvidenceReason') missingEvidenceReason FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) evidence,
      JSON_QUERY((SELECT ApplicationId applicationId,ItemId itemId,BillId billId,CONVERT(nvarchar(60),Amount) amount,Ordinal ordinal FROM @Targets ORDER BY Position FOR JSON PATH)) allocations
      FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    DECLARE @Lines nvarchar(max)=(SELECT Ordinal ordinal,AccountId accountId,AccountVersion accountVersion,CONVERT(nvarchar(60),Debit) debit,CONVERT(nvarchar(60),Credit) credit FROM @Line ORDER BY Ordinal FOR JSON PATH),
      @Controls nvarchar(max)=(SELECT Ordinal ordinal,ItemId itemId FROM @Targets ORDER BY Position FOR JSON PATH),@KernelRequest uniqueidentifier=NEWID();
    IF NOT EXISTS(SELECT 1 FROM @Targets) SET @Controls=NULL;
    DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
    INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@KernelRequest,@PaymentPermission,N'Supplier.Payment',1,@Canonical,
      N'SupplierPayment',@Payment,@Revision,N'Payment',1,@Config,@Currency,@PaymentDate,@Effective,@Date,NULL,NULL,@Snapshot,@Lines,@Controls;
    UPDATE s SET RecordedAtUtc=@Now FROM Accounting.SourceEvents s JOIN @Posted p ON p.SourceEventId=s.Id WHERE s.TenantId=@TenantId;
    UPDATE j SET RecordedAtUtc=@Now FROM Accounting.JournalEntries j JOIN @Posted p ON p.JournalId=j.Id WHERE j.TenantId=@TenantId;
    UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND RequestId=@KernelRequest;
    UPDATE a SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents a JOIN @Posted p ON p.JournalId=a.TargetId WHERE a.TenantId=@TenantId AND a.Action='Accounting.PostJournal';
    DECLARE @Events nvarchar(max)=(SELECT @Payment paymentId,JournalId journalId FROM @Posted FOR JSON PATH);
    EXEC Purchasing.AppendSupplierEventGroup @TenantId,@Group,@Now,@Events;
    SET @Result=(SELECT @Payment paymentId,@Revision paymentRevisionId,@Payment advanceItemId,@Group groupId,@Now recordedAtUtc,
      JSON_QUERY(COALESCE((SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),ApplicationId)+N'"'),N',') WITHIN GROUP(ORDER BY Position)+N']' FROM @Targets),N'[]')) applicationIds,
      JSON_QUERY((SELECT N'["'+CONVERT(nvarchar(36),JournalId)+N'"]' FROM @Posted)) journalIds,
      (SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierPaymentVersions WHERE TenantId=@TenantId AND PaymentId=@Payment) version FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    DECLARE @FinancialEvidenceSet uniqueidentifier;
EXEC Accounting.CaptureFinancialEvidence 'SupplierPayment',@Payment,@Revision,@FinancialEvidenceSet OUTPUT;
INSERT Purchasing.SupplierFinancialReceipts(TenantId,RequestId,Operation,ActorId,CanonicalInput,InputSha256,GroupId,ResultJson,RecordedAtUtc)
      VALUES(@TenantId,@RequestId,'RecordSupplierPayment',@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Group,@Result,@Now);
    COMMIT; IF @OwnerRequestId IS NULL SELECT @Result ResultJson;
  END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
