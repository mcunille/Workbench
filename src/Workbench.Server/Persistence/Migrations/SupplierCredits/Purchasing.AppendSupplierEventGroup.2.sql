      THROW 51004,'Supplier reversal control evidence is not exact.',1;
    DECLARE @InverseMovements nvarchar(max)=(SELECT ItemId itemId,@GroupId groupId,PostingDate postingDate,
      CONVERT(nvarchar(60),Amount) amount FROM @InverseEffects FOR JSON PATH);
    EXEC Purchasing.AssertSupplierAvailability @TenantId,@InverseMovements;
    INSERT Purchasing.SupplierApplicationReversals(TenantId,Id,GroupId,ApplicationId,PostingDate,Reason,ActorId,RecordedAtUtc)
      SELECT @TenantId,Id,@GroupId,ApplicationId,PostingDate,Reason,ActorId,@RecordedAtUtc FROM @InverseApplications;
    INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
      SELECT @TenantId,Id,ItemId,@GroupId,'ReverseApplication',SourceEventId,PostingDate,Amount,@RecordedAtUtc FROM @InverseEffects;
    INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
      SELECT @TenantId,NEWID(),@GroupId,e.Id,e.JournalId,e.Ordinal,l.AccountId,l.AccountVersion,l.AccountPurpose,e.Amount
      FROM @InverseEffects e JOIN Accounting.JournalLines l ON l.TenantId=@TenantId AND l.JournalId=e.JournalId AND l.Ordinal=e.Ordinal;
    UPDATE v SET ItemId=v.ItemId FROM Purchasing.SupplierItemVersions v WHERE v.TenantId=@TenantId
      AND EXISTS(SELECT 1 FROM @InverseEffects e WHERE e.ItemId=v.ItemId);
    UPDATE v SET ApplicationId=v.ApplicationId FROM Purchasing.SupplierApplicationVersions v WHERE v.TenantId=@TenantId
      AND EXISTS(SELECT 1 FROM @InverseApplications a WHERE a.ApplicationId=v.ApplicationId);
    RETURN;
  END;  IF JSON_VALUE(@Events,'$[0].paymentInverseJournalId') IS NOT NULL
  BEGIN
    IF @Count<>1 OR (SELECT COUNT(*) FROM OPENJSON(JSON_QUERY(@Events,'$[0]')))<>2
      OR EXISTS(SELECT 1 FROM OPENJSON(JSON_QUERY(@Events,'$[0]')) WHERE [key] NOT IN('paymentInverseJournalId','compensations'))
      THROW 51000,'Invalid payment inverse identity.',1;
    DECLARE @CorrectionJournal uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Events,'$[0].paymentInverseJournalId')),
      @OriginalPayment uniqueidentifier,@OriginalPaymentSource uniqueidentifier,@OriginalPaymentJournal uniqueidentifier,
      @PaymentInverseSource uniqueidentifier,@PaymentInverseDate date,@PaymentInverseActor uniqueidentifier,@PaymentInverseReason nvarchar(2000);
    SELECT @OriginalPayment=p.Id,@OriginalPaymentSource=c.OriginalSourceEventId,@OriginalPaymentJournal=c.OriginalJournalId,
      @PaymentInverseSource=c.ReversalSourceEventId,@PaymentInverseDate=c.PostingDate,@PaymentInverseActor=c.ActorId,@PaymentInverseReason=c.Reason
      FROM Accounting.CorrectionGroups c JOIN Accounting.SourceEvents s ON s.TenantId=c.TenantId AND s.Id=c.OriginalSourceEventId
      JOIN Purchasing.SupplierPayments p ON p.TenantId=s.TenantId AND p.Id=s.SourceId AND p.RevisionId=s.SourceRevision AND s.SourceKind='SupplierPayment' AND s.EventKind='Payment'
      JOIN Purchasing.SupplierFinancialReceipts r ON r.TenantId=c.TenantId AND r.GroupId=@GroupId AND r.ActorId=c.ActorId
        AND r.Operation='CorrectSupplierPayment' AND JSON_VALUE(r.ResultJson,'$.state')='pending'
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.paymentId'))=p.Id
      JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=r.TenantId AND g.Id=r.GroupId AND g.Operation='CorrectPayment' AND g.SourceId=p.Id AND g.RecordedAtUtc=@RecordedAtUtc
      WHERE c.TenantId=@TenantId AND c.ReversalJournalId=@CorrectionJournal AND c.RecordedAtUtc=@RecordedAtUtc;
    IF @OriginalPayment IS NULL OR (SELECT COUNT(*) FROM Purchasing.SupplierPaymentControl(@TenantId,@OriginalPayment))<>1
      OR EXISTS(SELECT 1 FROM Purchasing.SupplierPaymentCorrections WHERE TenantId=@TenantId AND OriginalPaymentId=@OriginalPayment)
      OR EXISTS(SELECT 1 FROM Purchasing.SupplierControlAttributions WHERE TenantId=@TenantId AND JournalId=@CorrectionJournal)
      THROW 51004,'Authentic complete payment inverse is unavailable.',1;
    IF EXISTS(SELECT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,Credit,Debit
        FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@OriginalPaymentJournal EXCEPT
        SELECT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit
        FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@CorrectionJournal)
      OR (SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@OriginalPaymentJournal)
        <>(SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@CorrectionJournal)
      THROW 51004,'Payment inverse changed historical journal evidence.',1;
    DECLARE @PaymentInverseEffects TABLE(Id uniqueidentifier PRIMARY KEY,OriginalMovementId uniqueidentifier,ItemId uniqueidentifier,
      EventKind varchar(24),Amount decimal(28,4),SourceEventId uniqueidentifier,JournalId uniqueidentifier,OriginalJournalId uniqueidentifier);
    INSERT @PaymentInverseEffects SELECT NEWID(),m.Id,m.ItemId,CASE m.EventKind WHEN 'Open' THEN 'ReverseSource' ELSE 'ReverseApplication' END,-m.Amount,
      @PaymentInverseSource,@CorrectionJournal,@OriginalPaymentJournal
      FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=@TenantId AND m.SourceEventId=@OriginalPaymentSource;
    DECLARE @Compensations TABLE(ApplicationId uniqueidentifier PRIMARY KEY,OriginalSource uniqueidentifier,OriginalJournal uniqueidentifier,
      InverseSource uniqueidentifier,InverseJournal uniqueidentifier UNIQUE);
    INSERT @Compensations SELECT a.Id,old.Id,oldJournal.Id,c.ReversalSourceEventId,c.ReversalJournalId
      FROM OPENJSON(@Events,'$[0].compensations') e
      JOIN Purchasing.SupplierApplicationReversals r ON r.TenantId=@TenantId AND r.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.reversalId'))
      JOIN Purchasing.SupplierApplications a ON a.TenantId=r.TenantId AND a.Id=r.ApplicationId AND a.FundingItemId=@OriginalPayment
      JOIN Purchasing.SupplierPayments p ON p.TenantId=a.TenantId AND p.Id=a.FundingItemId AND p.GroupId=a.GroupId
      JOIN Accounting.SourceEvents old ON old.TenantId=r.TenantId AND old.SourceKind='SupplierApplicationReversal' AND old.SourceId=r.Id
        AND old.SourceRevision=r.GroupId AND old.EventKind='Reverse' AND old.PostingDate=r.PostingDate AND old.RecordedAtUtc=r.RecordedAtUtc AND old.ActorId=r.ActorId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(old.SnapshotJson,'$.applicationId'))=a.Id
      JOIN Accounting.JournalEntries oldJournal ON oldJournal.TenantId=old.TenantId AND oldJournal.SourceEventId=old.Id
      JOIN Accounting.CorrectionGroups c ON c.TenantId=old.TenantId AND c.OriginalSourceEventId=old.Id AND c.OriginalJournalId=oldJournal.Id
        AND c.ReversalJournalId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.journalId')) AND c.RecordedAtUtc=@RecordedAtUtc
        AND c.ActorId=@PaymentInverseActor AND c.PostingDate=@PaymentInverseDate
      JOIN Accounting.CorrectionReceipts receipt ON receipt.TenantId=c.TenantId AND receipt.CorrectionId=c.Id AND receipt.SourceCommandKind='Supplier.CorrectPayment'
      JOIN Purchasing.SupplierFinancialReceipts owner ON owner.TenantId=c.TenantId AND owner.GroupId=@GroupId AND owner.Operation='CorrectSupplierPayment'
        AND JSON_VALUE(owner.ResultJson,'$.state')='pending' AND CONVERT(varbinary(max),owner.CanonicalInput)=CONVERT(varbinary(max),receipt.CanonicalInput);
    IF (SELECT COUNT(*) FROM @Compensations)<>(SELECT COUNT(*) FROM OPENJSON(@Events,'$[0].compensations'))
      OR (SELECT COUNT(*) FROM @Compensations)<>(SELECT COUNT(*) FROM Purchasing.SupplierApplications a
        JOIN Purchasing.SupplierPayments p ON p.TenantId=a.TenantId AND p.Id=a.FundingItemId AND p.GroupId=a.GroupId
        JOIN Purchasing.SupplierApplicationReversals r ON r.TenantId=a.TenantId AND r.ApplicationId=a.Id WHERE p.TenantId=@TenantId AND p.Id=@OriginalPayment)
      OR EXISTS(SELECT 1 FROM @Compensations c CROSS APPLY Purchasing.SupplierApplicationControl(@TenantId,c.ApplicationId) a WHERE
        (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=@TenantId AND m.SourceEventId=c.OriginalSource)<>2
        OR (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=c.OriginalJournal)<>2
        OR (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=c.InverseJournal)<>2
        OR EXISTS(SELECT 1 FROM (VALUES(a.DebtItemId,a.DebtAccountId,a.DebtAccountVersion,1),(a.FundingItemId,a.FundingAccountId,a.FundingAccountVersion,2)) x(ItemId,AccountId,Version,Ordinal)
          WHERE (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m JOIN Purchasing.SupplierControlAttributions t ON t.TenantId=m.TenantId AND t.MovementId=m.Id
            JOIN Accounting.JournalLines l ON l.TenantId=t.TenantId AND l.JournalId=t.JournalId AND l.Ordinal=t.Ordinal
            WHERE m.TenantId=@TenantId AND m.SourceEventId=c.OriginalSource AND m.ItemId=x.ItemId AND m.EventKind='ReverseApplication' AND m.Amount=a.Amount
              AND t.JournalId=c.OriginalJournal AND t.Ordinal=x.Ordinal AND t.AccountId=x.AccountId AND t.AccountVersion=x.Version AND t.Amount=m.Amount
              AND l.AccountId=t.AccountId AND l.AccountVersion=t.AccountVersion AND l.AccountPurpose=t.AccountPurpose
              AND ((x.Ordinal=1 AND l.Credit=a.Amount AND l.Debit=0) OR (x.Ordinal=2 AND l.Debit=a.Amount AND l.Credit=0)))<>1)
        OR EXISTS(SELECT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,Credit,Debit
          FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=c.OriginalJournal EXCEPT
          SELECT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit
          FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=c.InverseJournal))
      THROW 51004,'Embedded unapplication compensation is incomplete.',1;
    INSERT @PaymentInverseEffects SELECT NEWID(),m.Id,m.ItemId,'ReverseSource',-m.Amount,c.InverseSource,c.InverseJournal,c.OriginalJournal
      FROM @Compensations c JOIN Purchasing.SupplierItemMovements m ON m.TenantId=@TenantId AND m.SourceEventId=c.OriginalSource;
    DECLARE @PaymentInverseMovements nvarchar(max)=(SELECT ItemId itemId,@GroupId groupId,@PaymentInverseDate postingDate,
      CONVERT(nvarchar(60),Amount) amount FROM @PaymentInverseEffects FOR JSON PATH);
    EXEC Purchasing.AssertSupplierAvailability @TenantId,@PaymentInverseMovements;
    INSERT Purchasing.SupplierPaymentCorrections(TenantId,Id,OriginalPaymentId,GroupId,Reason,ActorId,RecordedAtUtc)
      VALUES(@TenantId,@GroupId,@OriginalPayment,@GroupId,@PaymentInverseReason,@PaymentInverseActor,@RecordedAtUtc);
    INSERT Purchasing.SupplierApplicationReversals(TenantId,Id,GroupId,ApplicationId,PostingDate,Reason,ActorId,RecordedAtUtc)
      SELECT @TenantId,NEWID(),@GroupId,a.Id,@PaymentInverseDate,@PaymentInverseReason,@PaymentInverseActor,@RecordedAtUtc
      FROM Purchasing.SupplierApplications a JOIN Purchasing.SupplierPayments p ON p.TenantId=a.TenantId AND p.Id=a.FundingItemId AND p.GroupId=a.GroupId
      WHERE a.TenantId=@TenantId AND p.Id=@OriginalPayment
        AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals r WHERE r.TenantId=a.TenantId AND r.ApplicationId=a.Id);
    INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
      SELECT @TenantId,Id,ItemId,@GroupId,EventKind,SourceEventId,@PaymentInverseDate,Amount,@RecordedAtUtc FROM @PaymentInverseEffects;
    INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
      SELECT @TenantId,NEWID(),@GroupId,e.Id,e.JournalId,a.Ordinal,a.AccountId,a.AccountVersion,a.AccountPurpose,-a.Amount
      FROM @PaymentInverseEffects e JOIN Purchasing.SupplierControlAttributions a ON a.TenantId=@TenantId AND a.MovementId=e.OriginalMovementId
        AND a.JournalId=e.OriginalJournalId;
    UPDATE v SET ItemId=v.ItemId FROM Purchasing.SupplierItemVersions v WHERE v.TenantId=@TenantId
      AND EXISTS(SELECT 1 FROM @PaymentInverseEffects e WHERE e.ItemId=v.ItemId);
    UPDATE v SET ApplicationId=v.ApplicationId FROM Purchasing.SupplierApplicationVersions v WHERE v.TenantId=@TenantId
      AND EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals r WHERE r.TenantId=@TenantId AND r.GroupId=@GroupId AND r.ApplicationId=v.ApplicationId);
    UPDATE Purchasing.SupplierPaymentVersions SET PaymentId=PaymentId WHERE TenantId=@TenantId AND PaymentId=@OriginalPayment;
    RETURN;
  END;  IF JSON_VALUE(@Events,'$[0].applicationId') IS NOT NULL
  BEGIN
    -- The named command has posted real journals through the protected kernel.
    -- Only immutable source/receipt identities enter this branch; no new item authority.
    IF EXISTS(SELECT 1 FROM OPENJSON(@Events) e WHERE (SELECT COUNT(*) FROM OPENJSON(e.value))<>2
        OR EXISTS(SELECT 1 FROM OPENJSON(e.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('applicationId','journalId')
          OR p.type<>1 OR DATALENGTH(p.value)<>72 OR TRY_CONVERT(uniqueidentifier,p.value) IS NULL))
      OR EXISTS(SELECT 1 FROM OPENJSON(@Events) e CROSS APPLY OPENJSON(e.value) p GROUP BY e.[key],p.[key] HAVING COUNT(*)>1)
      THROW 51000,'Invalid supplier application event identity.',1;
    DECLARE @Applications TABLE(Id uniqueidentifier PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,
      Amount decimal(28,4),PostingDate date,ActorId uniqueidentifier,SourceEventId uniqueidentifier,JournalId uniqueidentifier UNIQUE);
    INSERT @Applications
      SELECT s.SourceId,TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.fundingItemId')),
        TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.debtItemId')),
        TRY_CONVERT(decimal(28,4),JSON_VALUE(s.SnapshotJson,'$.amount')),j.PostingDate,s.ActorId,s.Id,j.Id
      FROM OPENJSON(@Events) e
      JOIN Accounting.JournalEntries j ON j.TenantId=@TenantId AND j.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.journalId')) AND j.RecordedAtUtc=@RecordedAtUtc
      JOIN Accounting.SourceEvents s ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId AND s.SourceKind='SupplierApplication'
        AND s.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(e.value,'$.applicationId')) AND s.SourceRevision=@GroupId
        AND s.EventKind='Apply' AND s.RecordedAtUtc=@RecordedAtUtc AND s.PostingDate=j.PostingDate
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.applicationId'))=s.SourceId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.SnapshotJson,'$.groupId'))=@GroupId
      JOIN Accounting.PostingReceipts p ON p.TenantId=@TenantId AND p.SourceEventId=s.Id AND p.JournalId=j.Id
        AND p.SourceCommandKind='Supplier.Apply' AND p.ActorId=s.ActorId AND p.RecordedAtUtc=@RecordedAtUtc;
    IF (SELECT COUNT(*) FROM @Applications)<>@Count OR (SELECT COUNT(DISTINCT ActorId) FROM @Applications)<>1
      OR EXISTS(SELECT 1 FROM @Applications WHERE FundingId IS NULL OR DebtId IS NULL OR Amount IS NULL OR Amount<=0 OR FundingId=DebtId)
      THROW 51004,'Supplier application source evidence is unavailable.',1;
    IF EXISTS(SELECT 1 FROM @Applications a WHERE NOT EXISTS(
      SELECT 1 FROM Purchasing.SupplierOpenItems f JOIN Purchasing.SupplierOpenItems d ON d.TenantId=f.TenantId
        AND d.SupplierId=f.SupplierId AND d.PurchaseOrderId=f.PurchaseOrderId AND d.Currency=f.Currency
      WHERE f.TenantId=@TenantId AND f.Id=a.FundingId AND d.Id=a.DebtId
        AND f.Kind IN('Advance','CreditReceivable') AND d.Kind IN('Payable','RefundClearing')
        AND f.SourcePostingDate<=a.PostingDate AND d.SourcePostingDate<=a.PostingDate
        AND (d.Kind='RefundClearing' OR (d.BillId IS NOT NULL AND d.SourceKind='SupplierBill' AND d.SourceId=d.BillId
          AND EXISTS(SELECT 1 FROM Purchasing.SupplierBillPostings b WHERE b.TenantId=d.TenantId AND b.BillId=d.BillId AND b.RevisionId=d.SourceRevisionId)))
        AND EXISTS(SELECT 1 FROM Accounting.JournalEntries j WHERE j.TenantId=@TenantId AND j.Id=a.JournalId AND j.Currency=f.Currency)))
      THROW 51004,'Supplier application items are unavailable.',1;
    DECLARE @Effects TABLE(Id uniqueidentifier PRIMARY KEY,ItemId uniqueidentifier,ApplicationId uniqueidentifier,
      JournalId uniqueidentifier,SourceEventId uniqueidentifier,Ordinal int,PostingDate date,Amount decimal(28,4));
    INSERT @Effects
      SELECT NEWID(),items.ItemId,a.Id,a.JournalId,a.SourceEventId,items.Ordinal,a.PostingDate,-a.Amount
      FROM @Applications a CROSS APPLY(VALUES(a.DebtId,1),(a.FundingId,2)) items(ItemId,Ordinal);
    IF EXISTS(SELECT 1 FROM @Effects e WHERE (SELECT COUNT(*) FROM Purchasing.SupplierItemControl(@TenantId,e.ItemId))<>1
      OR NOT EXISTS(SELECT 1 FROM Accounting.JournalLines l CROSS APPLY Purchasing.SupplierItemControl(@TenantId,e.ItemId) c
        WHERE l.TenantId=@TenantId AND l.JournalId=e.JournalId AND l.Ordinal=e.Ordinal
          AND l.AccountId=c.AccountId AND l.AccountVersion=c.AccountVersion AND l.AccountPurpose=c.AccountPurpose
          AND ((e.Ordinal=1 AND l.Debit=-e.Amount AND l.Credit=0) OR (e.Ordinal=2 AND l.Credit=-e.Amount AND l.Debit=0))))
      OR EXISTS(SELECT 1 FROM @Applications a WHERE (SELECT COUNT(*) FROM Accounting.JournalLines l WHERE l.TenantId=@TenantId AND l.JournalId=a.JournalId)<>2)
      OR EXISTS(SELECT 1 FROM @Effects e JOIN Purchasing.SupplierControlAttributions a ON a.TenantId=@TenantId AND a.JournalId=e.JournalId)
      THROW 51004,'Supplier application controls are not conserved.',1;
    DECLARE @ApplicationMovements nvarchar(max)=(SELECT ItemId itemId,@GroupId groupId,PostingDate postingDate,
      CONVERT(nvarchar(60),Amount) amount FROM @Effects FOR JSON PATH);
