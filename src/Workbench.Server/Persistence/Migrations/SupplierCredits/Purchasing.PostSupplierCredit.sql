-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PostSupplierCredit
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRY
    BEGIN TRAN;
    DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Lock int,
      @Resource nvarchar(255),@Canonical nvarchar(max),@Result nvarchar(max);
    IF @Tenant IS NULL THROW 51003,'Current supplier credit authority is required.',1;
    SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
    EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @Lock<0 THROW 51009,'Accounting is being changed. Retry.',1;
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierCreditsPost'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current supplier credit authority is required.',1; THROW; END CATCH;
    IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' THROW 51000,'Invalid credit request identity.',1;
    EXEC Purchasing.CanonicalizeSupplierCredit N'PostSupplierCredit',@Command,@Canonical OUTPUT;
    IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.allocations'))
    BEGIN
      EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
      THROW 51009,'Supplier credit allocation requires its integrated source participant.',1;
    END;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId)
    BEGIN
      IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId
        AND Operation='PostSupplierCredit' AND ActorId=@ActorId AND JSON_VALUE(ResultJson,'$.state') IS NULL
        AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
        THROW 51009,'Credit request identity has different or pending content.',1;
      SELECT @Result=ResultJson FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@RequestId;
      COMMIT; SELECT @Result ResultJson; RETURN;
    END;
    DECLARE @Credit uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.creditId')),
      @Revision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.creditRevisionId')),
      @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
      @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
      @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
      @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),
      @Document date=CONVERT(date,JSON_VALUE(@Canonical,'$.documentDate'),23),@Effective date=CONVERT(date,JSON_VALUE(@Canonical,'$.effectiveDate'),23),
      @Total decimal(28,4)=CONVERT(decimal(28,4),JSON_VALUE(@Canonical,'$.total')),@Scale int,@Policy nvarchar(max),
      @Reference nvarchar(200)=JSON_VALUE(@Canonical,'$.reference'),@Normalized nvarchar(200),@Group uniqueidentifier=NEWID(),@Now datetimeoffset,
      @Reason nvarchar(2000)=JSON_VALUE(@Canonical,'$.reason'),@Receivable uniqueidentifier,@ReceivableVersion uniqueidentifier;
    IF @Document>@Date OR @Effective>@Date THROW 51000,'Credit dates are invalid.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierCredits WHERE TenantId=@Tenant AND (Id=@Credit OR RevisionId=@Revision))
      THROW 51009,'Supplier credit source already exists.',1;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WHERE TenantId=@Tenant AND Id=@Po AND State='Ordered' AND IsDeleted=0)
      THROW 51004,'Purchase unavailable.',1;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND Id=@Po
      AND SupplierId=@Supplier AND Currency=@Currency AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
      THROW 51009,'Purchase revision changed.',1;
    SELECT @Policy=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND Version=@Config;
    IF @Policy IS NULL THROW 51009,'Accounting configuration changed.',1;
    SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Policy,'$.policies.scale'));
    IF @Scale NOT BETWEEN 0 AND 4 OR @Scale IS NULL OR JSON_VALUE(@Policy,'$.policies.currency') COLLATE Latin1_General_100_BIN2<>@Currency
      OR @Total<>ROUND(@Total,@Scale,1) OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.claims') WHERE
        CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount'))<>ROUND(CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),@Scale,1))
      THROW 51000,'Credit currency or precision is invalid.',1;
    EXEC Accounting.EnsureOpenPeriod @PostingDate=@Date,@ExpectedConfigurationVersion=@Config;
    SET @Normalized=Purchasing.NormalizeBillReference(@Reference);
    IF COALESCE(LEN(@Normalized),0)=0 THROW 51000,'A supplier credit reference is required.',1;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierCredits WHERE TenantId=@Tenant AND SupplierId=@Supplier AND NormalizedReference=@Normalized)
      OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.duplicateResolutions'))
      THROW 51009,'Supplier credit reference requires an exact recorded resolution.',1;
    SELECT @Receivable=a.Id,@ReceivableVersion=a.Version FROM OPENJSON(@Policy,'$.mappings') m
      JOIN Accounting.Accounts a ON a.TenantId=@Tenant AND a.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(m.value,'$.accountId'))
      WHERE JSON_VALUE(m.value,'$.slot')='SupplierCreditReceivable' AND a.Purpose='SupplierCreditReceivable' AND a.Type='Asset' AND a.ArchivedAtUtc IS NULL;
    IF @Receivable IS NULL THROW 51004,'Supplier credit account mapping unavailable.',1;
    DECLARE @Claims TABLE(Id uniqueidentifier PRIMARY KEY,BillId uniqueidentifier,BillRevisionId uniqueidentifier,
      EventId uniqueidentifier,ComponentKey nvarchar(200),Amount decimal(28,4),AccountId uniqueidentifier,
      AccountVersion uniqueidentifier,JournalId uniqueidentifier,Ordinal int);
    INSERT @Claims
      SELECT CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.claimId')),bill.BillId,bill.RevisionId,invoice.Id,component.ComponentKey,
        CONVERT(decimal(28,4),JSON_VALUE(claim.value,'$.amount')),line.AccountId,line.AccountVersion,line.JournalId,line.Ordinal
      FROM OPENJSON(@Canonical,'$.claims') claim
      JOIN Purchasing.SupplierBillPostings bill ON bill.TenantId=@Tenant AND bill.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.billId'))
        AND bill.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.billRevisionId'))
      JOIN Purchasing.SupplierBills owner ON owner.TenantId=bill.TenantId AND owner.Id=bill.BillId AND owner.State='Posted'
        AND owner.SupplierId=@Supplier AND owner.PurchaseOrderId=@Po AND owner.Currency=@Currency
      JOIN Purchasing.SupplierBillPostingEvents owned ON owned.TenantId=bill.TenantId AND owned.BillId=bill.BillId
      JOIN Purchasing.ActiveRecognitionSideEvents invoice ON invoice.TenantId=owned.TenantId AND invoice.Id=owned.EventId
        AND invoice.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId')) AND invoice.Side='Invoice' AND invoice.PostingDate<=@Date
      JOIN Purchasing.RecognitionComponents component ON component.TenantId=invoice.TenantId AND component.EventId=invoice.Id
        AND component.ComponentKey=JSON_VALUE(claim.value,'$.componentKey') AND component.Kind='BaseCost'
      JOIN Purchasing.RecognitionUnits unit ON unit.TenantId=invoice.TenantId AND unit.Id=invoice.UnitId AND unit.Classification='Expense'
      JOIN Purchasing.RecognitionMatches matched ON matched.TenantId=invoice.TenantId AND matched.InvoiceEventId=invoice.Id
      JOIN Purchasing.ActiveRecognitionSideEvents recognition ON recognition.TenantId=matched.TenantId AND recognition.Id=matched.RecognitionEventId AND recognition.PostingDate<=@Date
      JOIN Accounting.JournalLines line ON line.TenantId=recognition.TenantId AND line.JournalId=recognition.JournalId
        AND line.AccountType='Expense' AND line.Debit>0 AND line.Credit=0
      WHERE CONVERT(decimal(28,4),JSON_VALUE(claim.value,'$.amount'))<=component.Amount;
    IF (SELECT COUNT(*) FROM @Claims)<>(SELECT COUNT(*) FROM OPENJSON(@Canonical,'$.claims'))
      THROW 51004,'Supported posted expense components required.',1;
    IF (SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Claims)<>@Total
      THROW 51000,'Credit components must exactly equal the total.',1;
    IF EXISTS(SELECT 1 FROM @Claims claim JOIN Purchasing.RecognitionComponents original
        ON original.TenantId=@Tenant AND original.EventId=claim.EventId AND original.ComponentKey=claim.ComponentKey
      WHERE claim.Amount+COALESCE((SELECT SUM(CONVERT(decimal(38,4),c.Amount)) FROM Purchasing.SupplierCreditClaims c
        WHERE c.TenantId=@Tenant AND c.RecognitionEventId=claim.EventId AND c.ComponentKey=claim.ComponentKey),0)>original.Amount)
      THROW 51009,'Credit exceeds the remaining source component capacity.',1;
    DECLARE @Documents nvarchar(max),@Evidence nvarchar(max)=JSON_QUERY(@Canonical,'$.evidence');
    EXEC Purchasing.ValidateBillEvidence @Tenant,@Po,@Evidence,@Documents OUTPUT;
    DECLARE @Snapshot nvarchar(max)=(SELECT 1 schemaVersion,@Group groupId,JSON_QUERY(@Canonical) command,
      JSON_QUERY((SELECT Id id,Name name FROM Purchasing.Suppliers WHERE TenantId=@Tenant AND Id=@Supplier FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) supplier,
      JSON_QUERY((SELECT Id id,Revision revision,OrderDate orderDate,ContentJson content FROM Purchasing.DraftOrders WHERE TenantId=@Tenant AND Id=@Po FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) purchase,
      JSON_QUERY((SELECT JSON_QUERY(@Documents) documents,JSON_VALUE(@Evidence,'$.missingEvidenceReason') missingEvidenceReason FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) evidence
      FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    DECLARE @Line TABLE(Ordinal int,AccountId uniqueidentifier,AccountVersion uniqueidentifier,Debit decimal(28,4),Credit decimal(28,4));
    INSERT @Line VALUES(1,@Receivable,@ReceivableVersion,@Total,0);
    INSERT @Line SELECT 1+ROW_NUMBER() OVER(ORDER BY AccountId,AccountVersion),AccountId,AccountVersion,0,SUM(Amount) FROM @Claims GROUP BY AccountId,AccountVersion;
    DECLARE @Lines nvarchar(max)=(SELECT Ordinal ordinal,AccountId accountId,AccountVersion accountVersion,CONVERT(nvarchar(60),Debit) debit,CONVERT(nvarchar(60),Credit) credit FROM @Line ORDER BY Ordinal FOR JSON PATH);
    DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
    INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@RequestId,N'SupplierCreditsPost',N'Supplier.Credit',1,
      @Canonical,N'SupplierCredit',@Credit,@Revision,N'Credit',1,@Config,@Currency,@Document,@Effective,@Date,@Reference,@Reason,@Snapshot,@Lines;
    SELECT @Now=RecordedAtUtc FROM @Posted;
    INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc) VALUES(@Tenant,@Group,'RecordCredit',@Credit,@Now);
    INSERT Purchasing.SupplierFinancialReceipts(TenantId,RequestId,GroupId,Operation,ActorId,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
      VALUES(@Tenant,@RequestId,@Group,'PostSupplierCredit',@ActorId,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),N'{"state":"pending"}',@Now);
    INSERT Purchasing.SupplierCredits(TenantId,Id,RevisionId,SupplierId,PurchaseOrderId,Currency,DocumentDate,EffectiveDate,PostingDate,Total,Reference,NormalizedReference,Reason,SourceSnapshotJson,ActorId,GroupId,RecordedAtUtc)
      VALUES(@Tenant,@Credit,@Revision,@Supplier,@Po,@Currency,@Document,@Effective,@Date,@Total,@Reference,@Normalized,@Reason,@Snapshot,@ActorId,@Group,@Now);
    INSERT Purchasing.SupplierCreditClaims(TenantId,Id,CreditId,BillId,BillRevisionId,RecognitionEventId,ComponentKey,Treatment,Amount,AccountId,AccountVersion,OriginalJournalId,OriginalJournalOrdinal)
      SELECT @Tenant,Id,@Credit,BillId,BillRevisionId,EventId,ComponentKey,'Expense',Amount,AccountId,AccountVersion,JournalId,Ordinal FROM @Claims;
    DECLARE @Journal uniqueidentifier=(SELECT JournalId FROM @Posted),@Set uniqueidentifier;
    EXEC Purchasing.AppendSupplierCredit @Tenant,@Credit,@Journal;
    EXEC Accounting.CaptureFinancialEvidence 'SupplierCredit',@Credit,@Revision,@Set OUTPUT;
    SET @Result=(SELECT @Group groupId,@Credit sourceId,@Revision sourceRevisionId,
      CONVERT(varchar(18),CONVERT(binary(8),v.RowVersion),1) version,@Now recordedAtUtc,
      JSON_QUERY(N'["'+CONVERT(nvarchar(36),@Journal)+N'"]') journalIds,
      JSON_QUERY(N'["'+CONVERT(nvarchar(36),@Credit)+N'"]') itemIds,JSON_QUERY(N'[]') applicationIds,JSON_QUERY(N'[]') settlementIds
      FROM Purchasing.SupplierCreditVersions v WHERE v.TenantId=@Tenant AND v.CreditId=@Credit FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    UPDATE Purchasing.SupplierFinancialReceipts SET ResultJson=@Result WHERE TenantId=@Tenant AND RequestId=@RequestId AND JSON_VALUE(ResultJson,'$.state')='pending';
    IF @@ROWCOUNT<>1 THROW 51009,'Credit receipt was already finalized.',1;
    COMMIT; SELECT @Result ResultJson;
  END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
END;
