-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.AppendSupplierCredit
  @TenantId uniqueidentifier,@CreditId uniqueidentifier,@JournalId uniqueidentifier
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Supplier credit append requires its owner transaction.',1;
  DECLARE @Group uniqueidentifier,@Amount decimal(28,4),@Now datetimeoffset,@Date date,@Event uniqueidentifier,@Movement uniqueidentifier=NEWID();
  SELECT @Group=c.GroupId,@Amount=c.Total,@Now=c.RecordedAtUtc,@Date=c.PostingDate,@Event=s.Id
    FROM Purchasing.SupplierCredits c JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=c.TenantId AND g.Id=c.GroupId
      AND (g.Operation='RecordCredit' AND g.SourceId=c.Id OR g.Operation='CorrectCredit' AND EXISTS(
        SELECT 1 FROM Purchasing.SupplierSourceCorrections parent WHERE parent.TenantId=c.TenantId AND parent.GroupId=c.GroupId
          AND parent.ReplacementSourceId=c.Id AND parent.ReplacementSourceRevisionId=c.RevisionId
          AND parent.ActorId=c.ActorId AND parent.RecordedAtUtc=c.RecordedAtUtc)) AND g.RecordedAtUtc=c.RecordedAtUtc
    JOIN Purchasing.SupplierFinancialReceipts receipt ON receipt.TenantId=c.TenantId AND receipt.GroupId=c.GroupId
      AND receipt.ActorId=c.ActorId AND receipt.Operation='PostSupplierCredit' AND receipt.RecordedAtUtc=c.RecordedAtUtc
      AND JSON_VALUE(receipt.ResultJson,'$.state')='pending'
      AND CONVERT(varbinary(max),receipt.CanonicalInput)=CONVERT(varbinary(max),JSON_QUERY(c.SourceSnapshotJson,'$.command'))
    JOIN Accounting.SourceEvents s ON s.TenantId=c.TenantId AND s.SourceKind='SupplierCredit' AND s.SourceId=c.Id
      AND s.SourceRevision=c.RevisionId AND s.EventKind='Credit' AND s.ActorId=c.ActorId AND s.PostingDate=c.PostingDate
      AND s.DocumentDate=c.DocumentDate AND s.EffectiveDate=c.EffectiveDate AND s.RecordedAtUtc=c.RecordedAtUtc
      AND CONVERT(varbinary(max),s.SnapshotJson)=CONVERT(varbinary(max),c.SourceSnapshotJson)
    JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id AND j.Id=@JournalId
      AND j.Currency=c.Currency AND j.PostingDate=c.PostingDate AND j.RecordedAtUtc=c.RecordedAtUtc
    JOIN Accounting.PostingReceipts p ON p.TenantId=j.TenantId AND p.JournalId=j.Id AND p.SourceEventId=s.Id
      AND p.SourceCommandKind='Supplier.Credit' AND p.ActorId=c.ActorId AND p.RecordedAtUtc=c.RecordedAtUtc
      AND CONVERT(varbinary(max),p.CanonicalInput)=CONVERT(varbinary(max),receipt.CanonicalInput)
    WHERE c.TenantId=@TenantId AND c.Id=@CreditId;
  IF @Group IS NULL OR (SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@JournalId AND Debit>0)<>1
    OR NOT EXISTS(SELECT 1 FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@JournalId AND Ordinal=1
      AND AccountPurpose='SupplierCreditReceivable' AND Debit=@Amount AND Credit=0)
    OR EXISTS(SELECT 1 FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@JournalId AND AccountPurpose IN('Bank','Cash','CardLiability'))
    OR EXISTS(SELECT AccountId,AccountVersion,SUM(Amount) FROM Purchasing.SupplierCreditClaims WHERE TenantId=@TenantId AND CreditId=@CreditId GROUP BY AccountId,AccountVersion
      EXCEPT SELECT AccountId,AccountVersion,Credit FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@JournalId AND Credit>0)
    OR (SELECT COUNT(*) FROM (SELECT AccountId,AccountVersion FROM Purchasing.SupplierCreditClaims WHERE TenantId=@TenantId AND CreditId=@CreditId GROUP BY AccountId,AccountVersion) accounts)
      <>(SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@JournalId AND Credit>0)
    THROW 51004,'Authentic complete supplier credit posting required.',1;
  INSERT Purchasing.SupplierOpenItems(TenantId,Id,Kind,SupplierId,PurchaseOrderId,BillId,Currency,SourceKind,SourceId,SourceRevisionId,SourcePostingDate,DueDate,SourceSnapshotJson,RecordedAtUtc)
    SELECT TenantId,Id,'CreditReceivable',SupplierId,PurchaseOrderId,NULL,Currency,'SupplierCredit',Id,RevisionId,PostingDate,NULL,SourceSnapshotJson,RecordedAtUtc
    FROM Purchasing.SupplierCredits WHERE TenantId=@TenantId AND Id=@CreditId;
  INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
    VALUES(@TenantId,@Movement,@CreditId,@Group,'Open',@Event,@Date,@Amount,@Now);
  INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
    SELECT @TenantId,NEWID(),@Group,@Movement,@JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,@Amount
    FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@JournalId AND Ordinal=1;
  INSERT Purchasing.SupplierItemVersions(TenantId,ItemId) VALUES(@TenantId,@CreditId);
  INSERT Purchasing.SupplierCreditVersions(TenantId,CreditId) VALUES(@TenantId,@CreditId);
END;
