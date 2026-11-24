-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.RecordSupplierRefundRepayment
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Result nvarchar(max);
  EXEC Purchasing.RecordSupplierCashCore @ActorId,@SessionId,@RequestId,@Command,'SupplierRefundRepayment',@Result OUTPUT;
END;
