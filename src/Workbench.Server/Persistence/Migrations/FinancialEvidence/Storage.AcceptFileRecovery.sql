-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Storage].[AcceptFileRecovery]
    @ReportId uniqueidentifier, @Generation bigint, @Fingerprint varchar(64),
    @TargetAlias nvarchar(64), @Missing nvarchar(max)
WITH EXECUTE AS OWNER AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @@TRANCOUNT=0 THROW 50043, 'Recovery requires a guarded transaction.', 1;
    IF @ReportId IS NULL OR @TargetAlias IS NULL OR LEN(@TargetAlias)=0 OR ISJSON(@Missing)<>1
        THROW 50043, 'Invalid recovery acceptance.', 1;
    IF EXISTS (SELECT 1 FROM [Security].[FileRecoveryReports] WITH (UPDLOCK,HOLDLOCK)
        WHERE ReportId=@ReportId AND Generation=@Generation AND Fingerprint=@Fingerprint AND TargetAlias=@TargetAlias)
        RETURN;
    DECLARE @Inventory nvarchar(max);
    EXEC [Storage].[ReadRecoveryInventory] @Inventory OUTPUT;
    IF JSON_VALUE(@Inventory,'$.Generation')<>CONVERT(nvarchar(20),@Generation)
        OR @Fingerprint IS NULL OR CONVERT(varchar(64),HASHBYTES('SHA2_256',@Inventory),2)<>@Fingerprint
        THROW 50043, 'Recovery inventory changed.', 1;
    IF EXISTS (SELECT 1 FROM [Storage].[Revisions] WHERE State=0)
        THROW 50043, 'Resolve pending revisions before completing recovery.', 1;
    DECLARE @Files TABLE (TenantId uniqueidentifier, RevisionId uniqueidentifier PRIMARY KEY, Reason varchar(16));
    INSERT @Files SELECT TenantId,RevisionId,Reason FROM OPENJSON(@Missing)
        WITH (TenantId uniqueidentifier,RevisionId uniqueidentifier,Reason varchar(16));
    IF EXISTS(SELECT 1 FROM @Files f LEFT JOIN [Storage].[Revisions] r ON r.Id=f.RevisionId AND r.TenantId=f.TenantId
        WHERE r.Id IS NULL OR (r.State<>1 AND Storage.FinanciallyProtectedRevision(r.TenantId,r.AttachmentId)=0) OR (Storage.FinanciallyProtectedRevision(r.TenantId,r.AttachmentId)=1 AND Storage.FinancialEvidenceDeletionAuthorized(r.TenantId,r.AttachmentId)=1) OR f.Reason IS NULL OR f.Reason NOT IN ('Missing','Corrupt'))
        THROW 50043, 'Invalid missing-file disposition.', 1;
    DELETE FROM [Storage].[RecoveryFiles];
    INSERT [Storage].[RecoveryFiles] SELECT TenantId,RevisionId,@ReportId,@Generation,Reason,SYSUTCDATETIME() FROM @Files;
    UPDATE [Storage].[Revisions] SET ProviderAlias=@TargetAlias WHERE State=1 OR Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1;
    INSERT [Security].[FileRecoveryReports] VALUES(@ReportId,@Generation,@Fingerprint,@TargetAlias,SYSUTCDATETIME());
    UPDATE [Security].[BlobRecoveryState] SET IsPending=0 WHERE Id=1;
    INSERT [Security].[SystemSecurityAuditEvents] (Id,Action,Outcome,OccurredAtUtc)
        VALUES(NEWID(),N'storage.recovery-accepted',
            CASE WHEN EXISTS(SELECT 1 FROM @Files) THEN N'WithMissingFiles' ELSE N'Succeeded' END,SYSUTCDATETIME());
END;
