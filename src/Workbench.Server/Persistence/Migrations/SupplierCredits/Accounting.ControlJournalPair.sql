-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Accounting.ControlJournalPair(
  @FirstId uniqueidentifier,@FirstVersion uniqueidentifier,@SecondId uniqueidentifier,@SecondVersion uniqueidentifier,@Amount decimal(28,4))
RETURNS TABLE AS RETURN
  SELECT position.Ordinal,position.AccountId,position.AccountVersion,
    CONVERT(decimal(28,4),CASE WHEN Direction>0 THEN Direction ELSE 0 END) Debit,
    CONVERT(decimal(28,4),CASE WHEN Direction<0 THEN -Direction ELSE 0 END) Credit
  FROM (VALUES(1,@FirstId,@FirstVersion,@Amount),(2,@SecondId,@SecondVersion,-@Amount))
    position(Ordinal,AccountId,AccountVersion,Direction);
