ALTER PROCEDURE Purchasing.AppendSupplierEventGroup
  @TenantId uniqueidentifier,@GroupId uniqueidentifier,@RecordedAtUtc datetimeoffset,@Events nvarchar(max)
WITH EXECUTE AS OWNER
AS
BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR @GroupId IS NULL OR @RecordedAtUtc IS NULL
    OR @TenantId='00000000-0000-0000-0000-000000000000'
    OR @GroupId='00000000-0000-0000-0000-000000000000'
    OR @Events IS NULL OR DATALENGTH(@Events)>262144 OR ISJSON(@Events,ARRAY)<>1
    THROW 51000,'Invalid supplier event group envelope.',1;
  IF APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Supplier event group requires the tenant accounting lock.',1;
  DECLARE @Count int=(SELECT COUNT(*) FROM OPENJSON(@Events));
  IF @Count<1 OR @Count>1000 OR EXISTS(SELECT 1 FROM OPENJSON(@Events) WHERE [type]<>5)
    THROW 51000,'Invalid supplier event count or shape.',1;  IF JSON_VALUE(@Events,'$[0].paymentId') IS NOT NULL
  BEGIN
    IF @Count<>1 OR (SELECT COUNT(*) FROM OPENJSON(JSON_QUERY(@Events,'$[0]')))<>2
      OR EXISTS(SELECT 1 FROM OPENJSON(JSON_QUERY(@Events,'$[0]')) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN('paymentId','journalId')
        OR type<>1 OR DATALENGTH(value)<>72 OR TRY_CONVERT(uniqueidentifier,value) IS NULL)
      THROW 51000,'Invalid supplier payment event identity.',1;
    DECLARE @Pay uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Events,'$[0].paymentId')),
      @PayJournal uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Events,'$[0].journalId')),
      @PaySource uniqueidentifier,@PayRevision uniqueidentifier,@PaySnapshot nvarchar(max),@PayActor uniqueidentifier,
      @PayCurrency varchar(3),@PayDate date,@PayPaymentDate date,@PayEffective date;
    SELECT @PaySource=s.Id,@PayRevision=s.SourceRevision,@PaySnapshot=s.SnapshotJson,@PayActor=s.ActorId,
      @PayCurrency=j.Currency,@PayDate=j.PostingDate,@PayPaymentDate=s.DocumentDate,@PayEffective=s.EffectiveDate
      FROM Accounting.SourceEvents s JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id AND j.Id=@PayJournal
        AND j.RecordedAtUtc=@RecordedAtUtc AND j.PostingDate=s.PostingDate AND j.DocumentDate=s.DocumentDate AND j.EffectiveDate=s.EffectiveDate
      JOIN Accounting.PostingReceipts r ON r.TenantId=s.TenantId AND r.SourceEventId=s.Id AND r.JournalId=j.Id
        AND r.SourceCommandKind='Supplier.Payment' AND r.ActorId=s.ActorId AND r.RecordedAtUtc=@RecordedAtUtc
      WHERE s.TenantId=@TenantId AND s.SourceKind='SupplierPayment' AND s.SourceId=@Pay AND s.EventKind='Payment' AND s.RecordedAtUtc=@RecordedAtUtc
        AND CONVERT(varbinary(max),r.CanonicalInput)=CONVERT(varbinary(max),JSON_QUERY(s.SnapshotJson,'$.command'));
    IF @PaySource IS NULL OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.groupId'))<>@GroupId
      OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.paymentId'))<>@Pay
      OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.paymentRevisionId'))<>@PayRevision
      OR JSON_VALUE(@PaySnapshot,'$.command.operation')<>'RecordSupplierPayment'
      THROW 51004,'Authentic supplier payment source is unavailable.',1;
    DECLARE @PayAmount decimal(28,4)=TRY_CONVERT(decimal(28,4),JSON_VALUE(@PaySnapshot,'$.command.amount')),
      @PaySupplier uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.supplierId')),
      @PayPo uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.purchaseOrderId')),
      @PayFunding uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.fundingAccount.id')),
      @PayAdvance uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.advanceAccount.id'));
    IF @PayAmount IS NULL OR @PayAmount<=0 OR @PaySupplier IS NULL OR @PayPo IS NULL
      OR NOT EXISTS(SELECT 1 FROM Accounting.Accounts a WHERE a.TenantId=@TenantId AND a.Id=@PayAdvance AND a.ArchivedAtUtc IS NULL
        AND a.Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.advanceAccount.version')) AND a.Type='Asset' AND a.Purpose='SupplierAdvance'
        AND a.Code=JSON_VALUE(@PaySnapshot,'$.advanceAccount.code') AND a.Name=JSON_VALUE(@PaySnapshot,'$.advanceAccount.name')
        AND JSON_VALUE(@PaySnapshot,'$.advanceAccount.type')=a.Type AND JSON_VALUE(@PaySnapshot,'$.advanceAccount.purpose')=a.Purpose)
      OR NOT EXISTS(SELECT 1 FROM Accounting.Configurations c CROSS APPLY OPENJSON(c.Payload,'$.mappings') m WHERE c.TenantId=@TenantId
        AND c.Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.command.expectedConfigurationVersion'))
        AND JSON_VALUE(m.value,'$.slot')='SupplierAdvance' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(m.value,'$.accountId'))=@PayAdvance)
      OR NOT EXISTS(SELECT 1 FROM Accounting.Accounts a WHERE a.TenantId=@TenantId AND a.Id=@PayFunding AND a.ArchivedAtUtc IS NULL
        AND a.Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.fundingAccount.version')) AND a.Type='Asset' AND a.Purpose IN('Bank','Cash')
        AND a.Code=JSON_VALUE(@PaySnapshot,'$.fundingAccount.code') AND a.Name=JSON_VALUE(@PaySnapshot,'$.fundingAccount.name'))
      THROW 51004,'Payment account source snapshots are unavailable.',1;
    DECLARE @PayApplications TABLE(Id uniqueidentifier PRIMARY KEY,ItemId uniqueidentifier UNIQUE,BillId uniqueidentifier,Amount decimal(28,4),Ordinal int,
      AccountId uniqueidentifier,AccountVersion uniqueidentifier);
    INSERT @PayApplications SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.applicationId')),
      TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.itemId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.billId')),
      TRY_CONVERT(decimal(28,4),JSON_VALUE(a.value,'$.amount')),TRY_CONVERT(int,JSON_VALUE(a.value,'$.ordinal')),c.AccountId,c.AccountVersion
      FROM OPENJSON(@PaySnapshot,'$.allocations') a CROSS APPLY Purchasing.SupplierItemControl(@TenantId,TRY_CONVERT(uniqueidentifier,JSON_VALUE(a.value,'$.itemId'))) c;
    IF (SELECT COUNT(*) FROM @PayApplications)<>(SELECT COUNT(*) FROM OPENJSON(@PaySnapshot,'$.command.allocations'))
      OR EXISTS(SELECT 1 FROM @PayApplications a WHERE Amount IS NULL OR Amount<=0 OR Ordinal IS NULL OR NOT EXISTS(
        SELECT 1 FROM OPENJSON(@PaySnapshot,'$.command.allocations') original
        JOIN Purchasing.SupplierOpenItems i ON i.TenantId=@TenantId AND i.Id=a.ItemId AND i.Kind='Payable' AND i.BillId=a.BillId AND i.SourceKind='SupplierBill'
          AND i.SourceId=i.BillId AND i.SupplierId=@PaySupplier AND i.PurchaseOrderId=@PayPo AND i.Currency=@PayCurrency AND i.SourcePostingDate<=@PayDate
        JOIN Purchasing.SupplierBillPostings b ON b.TenantId=i.TenantId AND b.BillId=i.BillId AND b.RevisionId=i.SourceRevisionId
        WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.itemId'))=a.ItemId AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.billId'))=a.BillId
          AND TRY_CONVERT(decimal(28,4),JSON_VALUE(original.value,'$.amount'))=a.Amount))
      THROW 51004,'Complete immediate payment allocations are unavailable.',1;
    DECLARE @PayAllocated decimal(38,4)=COALESCE((SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @PayApplications),0);
    IF @PayAllocated>@PayAmount THROW 51009,'Payment allocations exceed gross funding.',1;
    DECLARE @PayNet decimal(28,4)=@PayAmount-@PayAllocated;
    DECLARE @PayExpected TABLE(AccountId uniqueidentifier,AccountVersion uniqueidentifier,Debit decimal(28,4),Credit decimal(28,4));
    INSERT @PayExpected SELECT AccountId,AccountVersion,SUM(Amount),0 FROM @PayApplications GROUP BY AccountId,AccountVersion;
    IF @PayNet>0 INSERT @PayExpected VALUES(@PayAdvance,CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.advanceAccount.version')),@PayNet,0);
    INSERT @PayExpected VALUES(@PayFunding,CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.fundingAccount.version')),0,@PayAmount);
    IF EXISTS(SELECT AccountId,AccountVersion,Debit,Credit FROM @PayExpected EXCEPT
        SELECT AccountId,AccountVersion,Debit,Credit FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@PayJournal)
      OR (SELECT COUNT(*) FROM @PayExpected)<>(SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@PayJournal)
      OR EXISTS(SELECT 1 FROM @PayApplications a WHERE NOT EXISTS(SELECT 1 FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=@PayJournal AND l.Ordinal=a.Ordinal AND l.AccountId=a.AccountId AND l.AccountVersion=a.AccountVersion AND l.AccountPurpose='SupplierPayable'))
      OR EXISTS(SELECT 1 FROM Purchasing.SupplierControlAttributions WHERE TenantId=@TenantId AND JournalId=@PayJournal)
      THROW 51004,'Supplier payment journal controls are not conserved.',1;
    DECLARE @PayEffects TABLE(Id uniqueidentifier PRIMARY KEY,ItemId uniqueidentifier,EventKind varchar(24),Amount decimal(28,4),Ordinal int);
    INSERT @PayEffects VALUES(NEWID(),@Pay,'Open',@PayAmount,NULL);
    INSERT @PayEffects SELECT NEWID(),a.ItemId,'Apply',-a.Amount,a.Ordinal FROM @PayApplications a
      UNION ALL SELECT NEWID(),@Pay,'Apply',-Amount,NULL FROM @PayApplications;
    DECLARE @PayMovements nvarchar(max)=(SELECT ItemId itemId,@GroupId groupId,@PayDate postingDate,CONVERT(nvarchar(60),Amount) amount FROM @PayEffects FOR JSON PATH);
    EXEC Purchasing.AssertSupplierAvailability @TenantId,@PayMovements;
    IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialGroups WHERE TenantId=@TenantId AND Id=@GroupId)
    BEGIN
      DECLARE @PayOwner uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.correctionRequestId')),
        @PayOwnerCommand uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.correctionCommandRequestId')),
        @PayOwnerGroup uniqueidentifier,@PayOwnerInstant datetimeoffset,@PayCanonical nvarchar(max)=JSON_QUERY(@PaySnapshot,'$.command');
      EXEC Purchasing.RequireSupplierComposition @PayActor,@PayOwner,@PayOwnerCommand,N'RecordSupplierPayment',@PayCanonical,@PayOwnerGroup OUTPUT,@PayOwnerInstant OUTPUT;
      IF @PayOwnerGroup<>@GroupId OR @PayOwnerInstant<>@RecordedAtUtc THROW 51004,'Payment replacement group disagrees with its owner.',1;
    END
    ELSE INSERT Purchasing.SupplierFinancialGroups(TenantId,Id,Operation,SourceId,RecordedAtUtc) VALUES(@TenantId,@GroupId,'RecordPayment',@Pay,@RecordedAtUtc);
    INSERT Purchasing.SupplierPayments(TenantId,Id,RevisionId,SupplierId,PurchaseOrderId,Currency,PaymentDate,EffectiveDate,PostingDate,Amount,Method,
      FundingAccountId,FundingAccountVersion,FundingAccountPurpose,Reference,Notes,EvidenceJson,ActorId,GroupId,RecordedAtUtc)
      VALUES(@TenantId,@Pay,@PayRevision,@PaySupplier,@PayPo,@PayCurrency,@PayPaymentDate,@PayEffective,@PayDate,@PayAmount,
        JSON_VALUE(@PaySnapshot,'$.command.method'),@PayFunding,CONVERT(uniqueidentifier,JSON_VALUE(@PaySnapshot,'$.fundingAccount.version')),
        JSON_VALUE(@PaySnapshot,'$.fundingAccount.purpose'),JSON_VALUE(@PaySnapshot,'$.command.reference'),JSON_VALUE(@PaySnapshot,'$.command.notes'),@PaySnapshot,@PayActor,@GroupId,@RecordedAtUtc);
    INSERT Purchasing.SupplierPaymentVersions(TenantId,PaymentId) VALUES(@TenantId,@Pay);
    INSERT Purchasing.SupplierOpenItems(TenantId,Id,Kind,SupplierId,PurchaseOrderId,Currency,SourceKind,SourceId,SourceRevisionId,SourcePostingDate,SourceSnapshotJson,RecordedAtUtc)
      VALUES(@TenantId,@Pay,'Advance',@PaySupplier,@PayPo,@PayCurrency,'SupplierPayment',@Pay,@PayRevision,@PayDate,@PaySnapshot,@RecordedAtUtc);
    INSERT Purchasing.SupplierItemVersions(TenantId,ItemId) VALUES(@TenantId,@Pay);
    INSERT Purchasing.SupplierApplications(TenantId,Id,GroupId,FundingItemId,DebtItemId,PostingDate,Amount,ActorId,RecordedAtUtc)
      SELECT @TenantId,Id,@GroupId,@Pay,ItemId,@PayDate,Amount,@PayActor,@RecordedAtUtc FROM @PayApplications;
    INSERT Purchasing.SupplierApplicationVersions(TenantId,ApplicationId) SELECT @TenantId,Id FROM @PayApplications;
    UPDATE v SET ItemId=v.ItemId FROM Purchasing.SupplierItemVersions v JOIN @PayApplications a ON a.ItemId=v.ItemId WHERE v.TenantId=@TenantId;
    INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
      SELECT @TenantId,Id,ItemId,@GroupId,EventKind,@PaySource,@PayDate,Amount,@RecordedAtUtc FROM @PayEffects;
    -- Payment groups attribute NET control effects; the gross opening and
    -- immediate funding reductions are proved together, including zero-net.
    INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
      SELECT @TenantId,NEWID(),@GroupId,e.Id,@PayJournal,l.Ordinal,l.AccountId,l.AccountVersion,l.AccountPurpose,
        CASE WHEN e.EventKind='Open' THEN @PayNet ELSE e.Amount END
      FROM @PayEffects e JOIN Accounting.JournalLines l ON l.TenantId=@TenantId AND l.JournalId=@PayJournal
        AND ((e.Ordinal=l.Ordinal AND e.ItemId<>@Pay) OR (e.EventKind='Open' AND l.AccountId=@PayAdvance AND @PayNet>0));
    RETURN;
  END;  IF JSON_VALUE(@Events,'$[0].reversalId') IS NOT NULL
  BEGIN
    IF EXISTS(SELECT 1 FROM OPENJSON(@Events) e WHERE (SELECT COUNT(*) FROM OPENJSON(e.value))<>2
        OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('reversalId','journalId')
          OR p.type<>1 OR DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL))
      OR EXISTS(SELECT 1 FROM OPENJSON(@Events) e CROSS APPLY OPENJSON(e.value) p GROUP BY e.[key],p.[key] HAVING COUNT(*)>1)
      THROW 51000,'Invalid supplier reversal event identities.',1;
    DECLARE @InverseApplications TABLE(Id uniqueidentifier PRIMARY KEY,ApplicationId uniqueidentifier UNIQUE,
      FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4),PostingDate date,ActorId uniqueidentifier,
      Reason nvarchar(2000),SourceEventId uniqueidentifier,JournalId uniqueidentifier UNIQUE);
    INSERT @InverseApplications
      SELECT s.SourceId,a.Id,a.FundingItemId,a.DebtItemId,a.Amount,s.PostingDate,s.ActorId,s.Reason,s.Id,j.Id
      FROM OPENJSON(@Events) e
      JOIN Accounting.JournalEntries j ON j.TenantId=@TenantId AND j.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.journalId'))
        AND j.RecordedAtUtc=@RecordedAtUtc
      JOIN Accounting.SourceEvents s ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId
        AND s.SourceKind='SupplierApplicationReversal' AND s.EventKind='Reverse' AND s.SourceRevision=@GroupId
        AND s.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.reversalId'))
        AND s.RecordedAtUtc=@RecordedAtUtc AND s.PostingDate=j.PostingDate
      CROSS APPLY Purchasing.SupplierApplicationControl(@TenantId,TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.applicationId'))) a
      JOIN Accounting.PostingReceipts r ON r.TenantId=j.TenantId AND r.SourceEventId=s.Id AND r.JournalId=j.Id
        AND r.SourceCommandKind='Supplier.ReverseApplication' AND r.ActorId=s.ActorId AND r.RecordedAtUtc=@RecordedAtUtc
      WHERE a.PostingDate<=s.PostingDate AND a.Amount=TRY_CONVERT(decimal(28,4),JSON_VALUE(s.SnapshotJson,'$.amount'))
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.groupId'))=@GroupId;
    IF (SELECT COUNT(*) FROM @InverseApplications)<>@Count OR (SELECT COUNT(DISTINCT ActorId) FROM @InverseApplications)<>1
      OR EXISTS(SELECT 1 FROM @InverseApplications a JOIN Purchasing.SupplierApplicationReversals r
        ON r.TenantId=@TenantId AND r.ApplicationId=a.ApplicationId)
      OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts r JOIN Purchasing.SupplierFinancialGroups g
        ON g.TenantId=r.TenantId AND g.Id=r.GroupId
        WHERE r.TenantId=@TenantId AND r.GroupId=@GroupId AND g.RecordedAtUtc=@RecordedAtUtc
          AND JSON_VALUE(r.ResultJson,'$.state')='pending' AND r.ActorId=(SELECT TOP(1) ActorId FROM @InverseApplications)
          AND (r.Operation IN('ReverseSupplierApplication','CorrectSupplierPayment') OR r.Operation IN('CorrectSupplierCredit','CorrectSupplierBill')
            AND NOT EXISTS(SELECT 1 FROM @InverseApplications expected WHERE NOT EXISTS(
              SELECT 1 FROM OPENJSON(r.ResultJson,'$.plan.applications') owned
              WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(owned.value,'$.applicationId'))=expected.ApplicationId
                AND TRY_CONVERT(date,JSON_VALUE(owned.value,'$.postingDate'),23)=expected.PostingDate))))
      THROW 51004,'Authentic pending supplier reversal owner is unavailable.',1;
    DECLARE @InverseEffects TABLE(Id uniqueidentifier PRIMARY KEY,ItemId uniqueidentifier,ApplicationId uniqueidentifier,
      SourceEventId uniqueidentifier,JournalId uniqueidentifier,Ordinal int,PostingDate date,Amount decimal(28,4));
    INSERT @InverseEffects SELECT NEWID(),e.ItemId,a.ApplicationId,a.SourceEventId,a.JournalId,e.Ordinal,a.PostingDate,a.Amount
      FROM @InverseApplications a CROSS APPLY(VALUES(a.DebtId,1),(a.FundingId,2)) e(ItemId,Ordinal);
    IF EXISTS(SELECT 1 FROM @InverseEffects e CROSS APPLY Purchasing.SupplierItemControl(@TenantId,e.ItemId) c
      WHERE NOT EXISTS(SELECT 1 FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=e.JournalId AND l.Ordinal=e.Ordinal
        AND l.AccountId=c.AccountId AND l.AccountVersion=c.AccountVersion AND l.AccountPurpose=c.AccountPurpose
        AND ((e.Ordinal=1 AND l.Credit=e.Amount AND l.Debit=0) OR (e.Ordinal=2 AND l.Debit=e.Amount AND l.Credit=0))))
      OR EXISTS(SELECT 1 FROM @InverseApplications a WHERE (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=a.JournalId)<>2)
      OR EXISTS(SELECT 1 FROM @InverseApplications a JOIN Purchasing.SupplierControlAttributions c ON c.TenantId=@TenantId AND c.JournalId=a.JournalId)
