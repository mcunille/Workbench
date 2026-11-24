-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.BuildSupplierCreditJournalLines
  @ReceivableId uniqueidentifier,@ReceivableVersion uniqueidentifier,@Total decimal(28,4),
  @DerivedClaims nvarchar(max),@Lines nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  WITH lines AS(
    SELECT 1 Ordinal,@ReceivableId AccountId,@ReceivableVersion AccountVersion,@Total Debit,CONVERT(decimal(28,4),0) Credit
    UNION ALL
    SELECT 1+ROW_NUMBER() OVER(ORDER BY AccountId,AccountVersion),AccountId,AccountVersion,0,CONVERT(decimal(28,4),SUM(Amount))
    FROM OPENJSON(@DerivedClaims)
      WITH(AccountId uniqueidentifier '$.accountId',AccountVersion uniqueidentifier '$.accountVersion',Amount decimal(28,4) '$.amount')
    GROUP BY AccountId,AccountVersion
  )
  SELECT @Lines=(SELECT Ordinal ordinal,AccountId accountId,AccountVersion accountVersion,
    CONVERT(nvarchar(60),Debit) debit,CONVERT(nvarchar(60),Credit) credit FROM lines ORDER BY Ordinal FOR JSON PATH);
END;
