-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.BuildSupplierCashJournalLines
  @CashKind varchar(32),@FundingId uniqueidentifier,@FundingVersion uniqueidentifier,
  @Amount decimal(28,4),@ResolvedTargets nvarchar(max),@Lines nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Outgoing bit=CASE WHEN @CashKind='SupplierRefundRepayment' THEN 1 ELSE 0 END;
  WITH controls AS(
    SELECT AccountId,AccountVersion,CONVERT(decimal(28,4),SUM(Amount)) Amount,
      1+ROW_NUMBER() OVER(ORDER BY AccountId,AccountVersion) Ordinal
    FROM OPENJSON(@ResolvedTargets)
      WITH(AccountId uniqueidentifier '$.accountId',AccountVersion uniqueidentifier '$.accountVersion',Amount decimal(28,4) '$.amount')
    GROUP BY AccountId,AccountVersion
  ), lines AS(
    SELECT 1 Ordinal,@FundingId AccountId,@FundingVersion AccountVersion,
      CASE WHEN @Outgoing=0 THEN @Amount ELSE 0 END Debit,CASE WHEN @Outgoing=1 THEN @Amount ELSE 0 END Credit
    UNION ALL
    SELECT Ordinal,AccountId,AccountVersion,CASE WHEN @Outgoing=1 THEN Amount ELSE 0 END,
      CASE WHEN @Outgoing=0 THEN Amount ELSE 0 END FROM controls
  )
  SELECT @Lines=(SELECT Ordinal ordinal,AccountId accountId,AccountVersion accountVersion,
    CONVERT(nvarchar(60),Debit) debit,CONVERT(nvarchar(60),Credit) credit FROM lines ORDER BY Ordinal FOR JSON PATH);
END;
