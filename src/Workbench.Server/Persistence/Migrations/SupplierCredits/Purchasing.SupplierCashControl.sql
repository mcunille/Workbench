-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierCashControl(@TenantId uniqueidentifier,@CashSourceId uniqueidentifier)
RETURNS TABLE AS RETURN
  SELECT CashSourceId,JournalId,SourceEventId,Amount,FundingAccountId,FundingAccountVersion
    FROM Purchasing.SupplierIncomingCashControl(@TenantId,@CashSourceId)
  UNION ALL
  SELECT CashSourceId,JournalId,SourceEventId,Amount,FundingAccountId,FundingAccountVersion
    FROM Purchasing.SupplierRepaymentCashControl(@TenantId,@CashSourceId);
