-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Operations].[LockWork]
    @Id uniqueidentifier, @Owner uniqueidentifier, @Generation bigint
AS
BEGIN
    SET NOCOUNT ON;
    IF @@TRANCOUNT = 0 THROW 50022, 'Work execution requires a transaction.', 1;
    DECLARE @Tenant uniqueidentifier,@Attachment uniqueidentifier;
SELECT @Tenant=TenantId,@Attachment=AttachmentId FROM Operations.WorkItems WITH(UPDLOCK,HOLDLOCK)
WHERE Id=@Id AND LeaseOwner=@Owner AND Generation=@Generation AND State=1 AND LeaseExpiresAtUtc>SYSUTCDATETIME() AND Kind=1;
IF @Attachment IS NOT NULL EXEC Storage.RequireFinancialEvidenceDeletion @Tenant,@Attachment,1;
SELECT COUNT(*) FROM [Operations].[WorkItems] WITH (UPDLOCK, HOLDLOCK)
    WHERE [Id] = @Id AND [LeaseOwner] = @Owner AND [Generation] = @Generation
        AND [State] = 1 AND [LeaseExpiresAtUtc] > SYSUTCDATETIME();
END;
