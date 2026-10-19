-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PostSupplierCredit
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  EXEC Purchasing.PostSupplierCreditCore @ActorId,@SessionId,@RequestId,@Command;
END;
