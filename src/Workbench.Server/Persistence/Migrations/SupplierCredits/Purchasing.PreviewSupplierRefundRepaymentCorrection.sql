-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PreviewSupplierRefundRepaymentCorrection
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  EXEC Purchasing.PreviewSupplierCashCorrection @ActorId,@SessionId,@Command,N'CorrectSupplierRefundRepayment';
END;
