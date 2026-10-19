-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierCreditPosition(@TenantId uniqueidentifier,@RecognitionEventId uniqueidentifier,
  @ComponentKey nvarchar(200),@PostingDate date)
RETURNS TABLE AS RETURN
  SELECT invoice.Id RecognitionEventId,invoice.UnitId,component.ComponentKey,component.Kind,
    CONVERT(decimal(28,4),component.Amount+COALESCE(assigned.Adjustment,0)) Capacity,
    CASE WHEN matched.Id IS NULL THEN 'Prepayment' ELSE 'Expense' END Treatment,
    line.AccountId,line.AccountVersion,line.JournalId OriginalJournalId,line.Ordinal OriginalJournalOrdinal
  FROM Purchasing.ActiveRecognitionSideEvents invoice
  JOIN Purchasing.RecognitionUnits unit ON unit.TenantId=invoice.TenantId AND unit.Id=invoice.UnitId
  JOIN Purchasing.RecognitionComponents component ON component.TenantId=invoice.TenantId AND component.EventId=invoice.Id
    AND component.ComponentKey=@ComponentKey AND component.Kind IN('BaseCost','Freight','Charge','NonrecoverableTax','Rounding')
  OUTER APPLY(SELECT SUM(CASE WHEN c.Kind='Discount' THEN -CONVERT(decimal(38,4),c.Amount)
    WHEN c.Kind='Rounding' THEN CONVERT(decimal(38,4),c.Amount) ELSE 0 END) Adjustment
    FROM Purchasing.RecognitionComponents c WHERE c.TenantId=invoice.TenantId AND c.EventId=invoice.Id
      AND c.AssignedCostComponentKey=component.ComponentKey AND c.Kind IN('Discount','Rounding')) assigned
  OUTER APPLY(SELECT recognition.Id,recognition.JournalId FROM Purchasing.RecognitionMatches match
    JOIN Purchasing.ActiveRecognitionSideEvents recognition ON recognition.TenantId=match.TenantId AND recognition.Id=match.RecognitionEventId
    WHERE match.TenantId=invoice.TenantId AND match.InvoiceEventId=invoice.Id AND recognition.PostingDate<=@PostingDate) matched
  JOIN Accounting.JournalLines line ON line.TenantId=invoice.TenantId
    AND line.JournalId=CASE WHEN matched.Id IS NULL THEN invoice.JournalId ELSE matched.JournalId END
    AND line.Debit>0 AND line.Credit=0
    AND ((matched.Id IS NULL AND line.AccountId=TRY_CONVERT(uniqueidentifier,(SELECT JSON_VALUE(value,'$.accountId')
      FROM OPENJSON(invoice.EvidenceJson,'$.accountMappings') WHERE JSON_VALUE(value,'$.slot')='Prepayment')))
      OR (matched.Id IS NOT NULL AND unit.Classification='Expense' AND line.AccountType='Expense'))
  WHERE invoice.TenantId=@TenantId AND invoice.Id=@RecognitionEventId AND invoice.Side='Invoice' AND invoice.PostingDate<=@PostingDate;
