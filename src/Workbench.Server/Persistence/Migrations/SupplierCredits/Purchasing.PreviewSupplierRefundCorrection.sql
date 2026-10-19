-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PreviewSupplierRefundCorrection
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  EXEC Purchasing.PreviewSupplierCashCorrection @ActorId,@SessionId,@Command,N'CorrectSupplierRefund';
END;
