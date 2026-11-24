-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierProposedOpenPositions(@TenantId uniqueidentifier,@Movements nvarchar(max))
RETURNS TABLE AS RETURN
  WITH Proposed AS (
    SELECT ItemId,PostingDate,Amount FROM OPENJSON(@Movements)
      WITH(ItemId uniqueidentifier '$.itemId',PostingDate date '$.postingDate',Amount decimal(38,4) '$.amount')
  ), Boundaries AS (
    SELECT DISTINCT ItemId,PostingDate FROM Proposed
  )
  SELECT boundary.ItemId,boundary.PostingDate,SUM(movement.Amount) Balance
  FROM Boundaries boundary
  CROSS APPLY (
    SELECT proposed.Amount FROM Proposed proposed
      WHERE proposed.ItemId=boundary.ItemId AND proposed.PostingDate<=boundary.PostingDate
    UNION ALL
    SELECT CONVERT(decimal(38,4),stored.Amount) FROM Purchasing.SupplierItemMovements stored
      WHERE stored.TenantId=@TenantId AND stored.ItemId=boundary.ItemId AND stored.PostingDate<=boundary.PostingDate
  ) movement
  GROUP BY boundary.ItemId,boundary.PostingDate;
