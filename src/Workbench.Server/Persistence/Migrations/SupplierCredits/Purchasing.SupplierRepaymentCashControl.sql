-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierRepaymentCashControl(@TenantId uniqueidentifier,@CashSourceId uniqueidentifier)
RETURNS TABLE AS RETURN
  SELECT c.Id CashSourceId,journal.Id JournalId,sourceEvent.Id SourceEventId,c.Amount,c.FundingAccountId,c.FundingAccountVersion
  FROM Purchasing.SupplierCashSources c
  JOIN Purchasing.SupplierFinancialGroups financialGroup ON financialGroup.TenantId=c.TenantId AND financialGroup.Id=c.GroupId
    AND ((financialGroup.SourceId=c.Id AND financialGroup.Operation='RecordRepayment') OR EXISTS(
      SELECT 1 FROM Purchasing.SupplierSourceCorrections parent WHERE parent.TenantId=c.TenantId AND parent.GroupId=c.GroupId
        AND parent.SourceKind='SupplierRefundRepayment' AND parent.OriginalCashSourceId=parent.SourceId
        AND parent.ReplacementSourceId=c.Id AND parent.ReplacementSourceRevisionId=c.RevisionId
        AND parent.ActorId=c.ActorId AND parent.RecordedAtUtc=c.RecordedAtUtc
        AND financialGroup.Operation IN('CorrectRefund','CorrectRepayment')
        AND parent.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(c.SourceSnapshotJson,'$.correctionId')))) AND financialGroup.RecordedAtUtc=c.RecordedAtUtc
  JOIN Purchasing.SupplierFinancialReceipts receipt ON receipt.TenantId=c.TenantId AND receipt.GroupId=c.GroupId
    AND receipt.Operation='RecordSupplierRefundRepayment' AND receipt.ActorId=c.ActorId AND receipt.RecordedAtUtc=c.RecordedAtUtc
    AND CONVERT(varbinary(max),receipt.CanonicalInput)=CONVERT(varbinary(max),JSON_QUERY(c.SourceSnapshotJson,'$.command'))
  JOIN Accounting.SourceEvents sourceEvent ON sourceEvent.TenantId=c.TenantId AND sourceEvent.SourceKind=c.Kind AND sourceEvent.SourceId=c.Id
    AND sourceEvent.SourceRevision=c.RevisionId AND sourceEvent.EventKind='RefundRepayment' AND sourceEvent.ActorId=c.ActorId
    AND sourceEvent.DocumentDate=c.ActualDate AND sourceEvent.EffectiveDate=c.EffectiveDate AND sourceEvent.PostingDate=c.PostingDate
    AND sourceEvent.RecordedAtUtc=c.RecordedAtUtc AND CONVERT(varbinary(max),sourceEvent.SnapshotJson)=CONVERT(varbinary(max),c.SourceSnapshotJson)
  JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id
    AND journal.Currency=c.Currency AND journal.PostingDate=c.PostingDate AND journal.RecordedAtUtc=c.RecordedAtUtc
  JOIN Accounting.PostingReceipts posted ON posted.TenantId=c.TenantId AND posted.SourceEventId=sourceEvent.Id AND posted.JournalId=journal.Id
    AND posted.SourceCommandKind='Supplier.RefundRepayment' AND posted.ActorId=c.ActorId AND posted.RecordedAtUtc=c.RecordedAtUtc
    AND CONVERT(varbinary(max),posted.CanonicalInput)=CONVERT(varbinary(max),receipt.CanonicalInput)
  JOIN Accounting.JournalLines funding ON funding.TenantId=c.TenantId AND funding.JournalId=journal.Id AND funding.Ordinal=1
    AND funding.AccountId=c.FundingAccountId AND funding.AccountVersion=c.FundingAccountVersion AND funding.AccountPurpose=c.FundingAccountPurpose
    AND funding.AccountType='Asset' AND funding.AccountPurpose IN('Bank','Cash') AND funding.Debit=0 AND funding.Credit=c.Amount
  WHERE c.TenantId=@TenantId AND c.Id=@CashSourceId AND c.Kind='SupplierRefundRepayment'
    AND (SELECT SUM(CONVERT(decimal(38,4),settlement.Amount)) FROM Purchasing.SupplierCashSettlements settlement
      WHERE settlement.TenantId=c.TenantId AND settlement.CashSourceId=c.Id)=c.Amount
    AND (SELECT COUNT(*) FROM Accounting.JournalLines WHERE TenantId=c.TenantId AND JournalId=journal.Id AND Credit>0)=1
    AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierCashSettlements settlement WHERE settlement.TenantId=c.TenantId AND settlement.CashSourceId=c.Id
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierItemMovements movement
        JOIN Purchasing.SupplierControlAttributions attribution ON attribution.TenantId=movement.TenantId AND attribution.MovementId=movement.Id AND attribution.GroupId=movement.GroupId
        JOIN Accounting.JournalLines line ON line.TenantId=attribution.TenantId AND line.JournalId=attribution.JournalId AND line.Ordinal=attribution.Ordinal
        CROSS APPLY Purchasing.SupplierRefundReleaseControl(c.TenantId,settlement.ItemId) control
        WHERE movement.TenantId=c.TenantId AND movement.Id=settlement.MovementId AND movement.ItemId=settlement.ItemId AND movement.GroupId=c.GroupId
          AND movement.EventKind='CashSettlement' AND movement.SourceEventId=sourceEvent.Id AND movement.Amount=-settlement.Amount
          AND movement.PostingDate=c.PostingDate AND movement.RecordedAtUtc=c.RecordedAtUtc
          AND attribution.JournalId=journal.Id AND attribution.Ordinal=settlement.JournalOrdinal AND attribution.Amount=movement.Amount
          AND settlement.JournalId=journal.Id AND settlement.AccountId=line.AccountId AND settlement.AccountVersion=line.AccountVersion
          AND attribution.AccountId=settlement.AccountId AND attribution.AccountVersion=settlement.AccountVersion AND attribution.AccountPurpose=line.AccountPurpose
          AND line.AccountId=control.AccountId AND line.AccountVersion=control.AccountVersion AND line.AccountCode=control.AccountCode
          AND line.AccountName=control.AccountName AND line.AccountType=control.AccountType AND line.AccountPurpose=control.AccountPurpose
          AND line.Debit>0 AND line.Credit=0))
    AND NOT EXISTS(SELECT line.Ordinal,line.AccountId,line.AccountVersion,line.Debit FROM Accounting.JournalLines line
      WHERE line.TenantId=c.TenantId AND line.JournalId=journal.Id AND line.Debit>0
      EXCEPT SELECT settlement.JournalOrdinal,settlement.AccountId,settlement.AccountVersion,SUM(CONVERT(decimal(38,4),settlement.Amount))
        FROM Purchasing.SupplierCashSettlements settlement WHERE settlement.TenantId=c.TenantId AND settlement.CashSourceId=c.Id
        GROUP BY settlement.JournalOrdinal,settlement.AccountId,settlement.AccountVersion);
