-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierRefundReleaseControl(@TenantId uniqueidentifier,@ItemId uniqueidentifier)
RETURNS @Control TABLE(AccountId uniqueidentifier,AccountVersion uniqueidentifier,AccountCode nvarchar(32) COLLATE Latin1_General_100_BIN2,
  AccountName nvarchar(160),AccountType nvarchar(40),AccountPurpose nvarchar(40))
AS BEGIN
  -- Materialize the typed release proof so unrelated item queries do not expand the cash owner graph.
  INSERT @Control
  SELECT line.AccountId,line.AccountVersion,line.AccountCode,line.AccountName,line.AccountType,line.AccountPurpose
  FROM Purchasing.SupplierCashSettlementReleases released
  JOIN Purchasing.SupplierCashSettlements settlement ON settlement.TenantId=released.TenantId AND settlement.Id=released.SettlementId
  CROSS APPLY Purchasing.SupplierIncomingCashControl(@TenantId,settlement.CashSourceId) cashControl
  JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=released.TenantId AND correction.Id=released.CorrectionId
    AND correction.GroupId=released.GroupId AND correction.SourceKind='SupplierCredit' AND correction.SourceId=settlement.ItemId
    AND correction.ActorId=released.ActorId AND correction.RecordedAtUtc=released.RecordedAtUtc
  JOIN Purchasing.SupplierFinancialGroups financialGroup ON financialGroup.TenantId=correction.TenantId AND financialGroup.Id=correction.GroupId
    AND financialGroup.Operation IN('CorrectCredit','CorrectBill') AND financialGroup.RecordedAtUtc=correction.RecordedAtUtc
  JOIN Purchasing.SupplierFinancialReceipts receipt ON receipt.TenantId=financialGroup.TenantId AND receipt.GroupId=financialGroup.Id
    AND (financialGroup.Operation='CorrectCredit' AND receipt.Operation='CorrectSupplierCredit'
      OR financialGroup.Operation='CorrectBill' AND receipt.Operation='CorrectSupplierBill')
    AND receipt.ActorId=correction.ActorId AND receipt.RecordedAtUtc=correction.RecordedAtUtc
    AND financialGroup.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(receipt.CanonicalInput,'$.sourceId'))
  JOIN Accounting.SourceEvents sourceEvent ON sourceEvent.TenantId=released.TenantId AND sourceEvent.SourceKind='SupplierRefundRelease'
    AND sourceEvent.SourceId=released.SettlementId AND sourceEvent.SourceRevision=correction.Id AND sourceEvent.EventKind='Release'
    AND sourceEvent.ActorId=released.ActorId AND sourceEvent.PostingDate=released.PostingDate AND sourceEvent.RecordedAtUtc=released.RecordedAtUtc
    AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(sourceEvent.SnapshotJson,'$.correctionId'))=correction.Id
    AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(sourceEvent.SnapshotJson,'$.settlementId'))=settlement.Id
  JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id AND journal.Id=released.JournalId
    AND journal.Currency=correction.Currency AND journal.PostingDate=released.PostingDate AND journal.RecordedAtUtc=released.RecordedAtUtc
  JOIN Accounting.PostingReceipts posted ON posted.TenantId=journal.TenantId AND posted.JournalId=journal.Id AND posted.SourceEventId=sourceEvent.Id
    AND posted.SourceCommandKind='Supplier.RefundRelease' AND posted.ActorId=released.ActorId AND posted.RecordedAtUtc=released.RecordedAtUtc
  JOIN Accounting.JournalLines line ON line.TenantId=journal.TenantId AND line.JournalId=journal.Id AND line.Ordinal=2
    AND line.AccountId=released.ClearingAccountId AND line.AccountVersion=released.ClearingAccountVersion
    AND line.AccountType='Liability' AND line.AccountPurpose='SupplierRefundClearing' AND line.Credit=released.Amount AND line.Debit=0
  JOIN Purchasing.SupplierOpenItems item ON item.TenantId=released.TenantId AND item.Id=released.SettlementId
    AND item.Kind='RefundClearing' AND item.SourceKind='SupplierRefundRelease' AND item.SourceId=released.SettlementId
    AND item.SourceRevisionId=correction.Id AND item.SupplierId=correction.SupplierId AND item.PurchaseOrderId=correction.PurchaseOrderId
    AND item.Currency=correction.Currency AND item.SourcePostingDate=released.PostingDate AND item.RecordedAtUtc=released.RecordedAtUtc
    AND CONVERT(varbinary(max),item.SourceSnapshotJson)=CONVERT(varbinary(max),sourceEvent.SnapshotJson)
  JOIN Purchasing.SupplierItemMovements opening ON opening.TenantId=item.TenantId AND opening.Id=released.OpeningMovementId
    AND opening.ItemId=item.Id AND opening.GroupId=correction.GroupId AND opening.EventKind='Open' AND opening.Amount=released.Amount
    AND opening.SourceEventId=sourceEvent.Id AND opening.PostingDate=released.PostingDate AND opening.RecordedAtUtc=released.RecordedAtUtc
  JOIN Purchasing.SupplierControlAttributions attribution ON attribution.TenantId=opening.TenantId AND attribution.MovementId=opening.Id
    AND attribution.GroupId=opening.GroupId AND attribution.JournalId=line.JournalId AND attribution.Ordinal=line.Ordinal
    AND attribution.AccountId=line.AccountId AND attribution.AccountVersion=line.AccountVersion AND attribution.AccountPurpose=line.AccountPurpose AND attribution.Amount=opening.Amount
  WHERE released.TenantId=@TenantId AND released.SettlementId=@ItemId AND released.Amount=settlement.Amount
    AND (correction.SourceId=financialGroup.SourceId OR EXISTS(SELECT 1 FROM OPENJSON(receipt.CanonicalInput,'$.sourceDecisions') decision
      WHERE JSON_VALUE(decision.value,'$.sourceKind')='SupplierCredit' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))=correction.SourceId))
    AND (SELECT COUNT(*) FROM Purchasing.SupplierItemMovements WHERE TenantId=@TenantId AND ItemId=@ItemId AND EventKind='Open')=1;
  RETURN;
END;
