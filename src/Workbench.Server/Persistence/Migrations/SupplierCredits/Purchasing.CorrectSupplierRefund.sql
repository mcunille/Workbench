-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.CorrectSupplierRefund
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  EXEC Purchasing.CorrectSupplierCashCorrection @ActorId,@SessionId,@RequestId,@Command,N'CorrectSupplierRefund';
END;
