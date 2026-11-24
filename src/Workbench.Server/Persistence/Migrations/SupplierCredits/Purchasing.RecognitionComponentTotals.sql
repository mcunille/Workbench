-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.RecognitionComponentTotals(@Components nvarchar(max))
RETURNS TABLE AS RETURN
  SELECT COALESCE(SUM(CASE WHEN Kind='Discount' THEN -CONVERT(decimal(38,4),Amount)
      WHEN Kind IN('BaseCost','Freight','Charge','NonrecoverableTax','Rounding') THEN CONVERT(decimal(38,4),Amount) ELSE 0 END),0) Cost,
    COALESCE(SUM(CASE WHEN Kind='RecoverableTax' THEN CONVERT(decimal(38,4),Amount) ELSE 0 END),0) Tax
  FROM OPENJSON(@Components) WITH(Kind varchar(32) '$.kind',Amount decimal(28,4) '$.amount');
