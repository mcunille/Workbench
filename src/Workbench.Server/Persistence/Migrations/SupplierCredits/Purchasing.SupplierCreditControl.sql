-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierCreditControl(@TenantId uniqueidentifier,@ItemId uniqueidentifier)
RETURNS TABLE AS RETURN
  SELECT l.AccountId,l.AccountVersion,l.AccountCode,l.AccountName,l.AccountType,l.AccountPurpose
  FROM Purchasing.SupplierCredits c
  JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=c.TenantId AND g.Id=c.GroupId AND g.SourceId=c.Id
    AND g.Operation='RecordCredit' AND g.RecordedAtUtc=c.RecordedAtUtc
  JOIN Purchasing.SupplierFinancialReceipts receipt ON receipt.TenantId=c.TenantId AND receipt.GroupId=c.GroupId
    AND receipt.Operation='PostSupplierCredit' AND receipt.ActorId=c.ActorId AND receipt.RecordedAtUtc=c.RecordedAtUtc
    AND CONVERT(varbinary(max),receipt.CanonicalInput)=CONVERT(varbinary(max),JSON_QUERY(c.SourceSnapshotJson,'$.command'))
  JOIN Purchasing.SupplierOpenItems i ON i.TenantId=c.TenantId AND i.Id=c.Id AND i.Kind='CreditReceivable'
    AND i.SourceKind='SupplierCredit' AND i.SourceId=c.Id AND i.SourceRevisionId=c.RevisionId
    AND i.SupplierId=c.SupplierId AND i.PurchaseOrderId=c.PurchaseOrderId AND i.Currency=c.Currency
    AND i.SourcePostingDate=c.PostingDate AND i.RecordedAtUtc=c.RecordedAtUtc
    AND CONVERT(varbinary(max),i.SourceSnapshotJson)=CONVERT(varbinary(max),c.SourceSnapshotJson)
  JOIN Accounting.SourceEvents s ON s.TenantId=c.TenantId AND s.SourceKind='SupplierCredit' AND s.SourceId=c.Id
    AND s.SourceRevision=c.RevisionId AND s.EventKind='Credit' AND s.ActorId=c.ActorId AND s.PostingDate=c.PostingDate
    AND s.DocumentDate=c.DocumentDate AND s.EffectiveDate=c.EffectiveDate AND s.RecordedAtUtc=c.RecordedAtUtc
    AND CONVERT(varbinary(max),s.SnapshotJson)=CONVERT(varbinary(max),c.SourceSnapshotJson)
  JOIN Accounting.JournalEntries j ON j.TenantId=s.TenantId AND j.SourceEventId=s.Id AND j.Currency=c.Currency
    AND j.PostingDate=c.PostingDate AND j.RecordedAtUtc=c.RecordedAtUtc
  JOIN Accounting.PostingReceipts posted ON posted.TenantId=s.TenantId AND posted.SourceEventId=s.Id AND posted.JournalId=j.Id
    AND posted.SourceCommandKind='Supplier.Credit' AND posted.ActorId=c.ActorId AND posted.RecordedAtUtc=c.RecordedAtUtc
    AND CONVERT(varbinary(max),posted.CanonicalInput)=CONVERT(varbinary(max),receipt.CanonicalInput)
  JOIN Accounting.JournalLines l ON l.TenantId=j.TenantId AND l.JournalId=j.Id AND l.Ordinal=1
    AND l.AccountPurpose='SupplierCreditReceivable' AND l.AccountType='Asset' AND l.Debit=c.Total AND l.Credit=0
  JOIN Purchasing.SupplierItemMovements m ON m.TenantId=c.TenantId AND m.ItemId=c.Id AND m.GroupId=c.GroupId
    AND m.EventKind='Open' AND m.SourceEventId=s.Id AND m.Amount=c.Total AND m.PostingDate=c.PostingDate AND m.RecordedAtUtc=c.RecordedAtUtc
  JOIN Purchasing.SupplierControlAttributions a ON a.TenantId=m.TenantId AND a.MovementId=m.Id AND a.GroupId=m.GroupId
    AND a.JournalId=l.JournalId AND a.Ordinal=l.Ordinal AND a.AccountId=l.AccountId AND a.AccountVersion=l.AccountVersion
    AND a.AccountPurpose=l.AccountPurpose AND a.Amount=c.Total
  WHERE c.TenantId=@TenantId AND c.Id=@ItemId
    AND (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements original WHERE original.TenantId=c.TenantId
      AND original.ItemId=c.Id AND original.EventKind='Open')=1;
