-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Purchasing.PostSupplierBill @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  EXEC Purchasing.PostSupplierBillCore @ActorId,@SessionId,@RequestId,@Command;
END;
