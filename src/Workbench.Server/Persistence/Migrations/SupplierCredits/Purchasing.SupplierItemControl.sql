ALTER FUNCTION Purchasing.SupplierItemControl(@TenantId uniqueidentifier,@ItemId uniqueidentifier)
RETURNS TABLE AS RETURN
  SELECT DISTINCT l.AccountId,l.AccountVersion,l.AccountCode,l.AccountName,l.AccountType,l.AccountPurpose
  FROM Purchasing.SupplierOpenItems i
  JOIN Purchasing.SupplierItemMovements m ON m.TenantId=i.TenantId AND m.ItemId=i.Id AND m.EventKind='Open' AND m.Amount>0
  JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=m.TenantId AND g.Id=m.GroupId AND g.RecordedAtUtc=m.RecordedAtUtc
  JOIN Purchasing.SupplierControlAttributions a ON a.TenantId=m.TenantId AND a.MovementId=m.Id AND a.GroupId=m.GroupId AND a.Amount=m.Amount
  JOIN Accounting.JournalEntries j ON j.TenantId=m.TenantId AND j.Id=a.JournalId AND j.SourceEventId=m.SourceEventId
    AND j.Currency=i.Currency AND j.PostingDate=m.PostingDate AND j.RecordedAtUtc=m.RecordedAtUtc
  JOIN Accounting.SourceEvents s ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId AND s.PostingDate=j.PostingDate AND s.RecordedAtUtc=j.RecordedAtUtc
  JOIN Accounting.JournalLines l ON l.TenantId=a.TenantId AND l.JournalId=a.JournalId AND l.Ordinal=a.Ordinal
    AND l.AccountId=a.AccountId AND l.AccountVersion=a.AccountVersion AND l.AccountPurpose=a.AccountPurpose
  WHERE i.TenantId=@TenantId AND i.Id=@ItemId AND m.PostingDate=i.SourcePostingDate
    AND l.AccountPurpose=CASE i.Kind WHEN 'Advance' THEN 'SupplierAdvance' WHEN 'Payable' THEN 'SupplierPayable'
      WHEN 'CreditReceivable' THEN 'SupplierCreditReceivable' WHEN 'RefundClearing' THEN 'SupplierRefundClearing' END
    AND ((i.Kind IN('Advance','CreditReceivable') AND l.Debit=m.Amount AND l.Credit=0 AND l.AccountType='Asset')
      OR (i.Kind IN('Payable','RefundClearing') AND l.Credit=m.Amount AND l.Debit=0 AND l.AccountType='Liability'))
    AND i.SourceKind NOT IN('SupplierPayment','SupplierCredit')
  UNION ALL SELECT AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose
    FROM Purchasing.SupplierPaymentControl(@TenantId,@ItemId)
  UNION ALL SELECT AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose
    FROM Purchasing.SupplierCreditControl(@TenantId,@ItemId);
