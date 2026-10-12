-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Storage].[ExportManifest] @AfterId uniqueidentifier = NULL
WITH EXECUTE AS OWNER AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (500) [TenantId], [Id], [ProviderAlias], [Length], [Sha256]
    FROM [Storage].[Revisions]
    WHERE ([State] = 1 OR Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1) AND NOT (Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1 AND Storage.FinancialEvidenceDeletionAuthorized(TenantId,AttachmentId)=1) AND (@AfterId IS NULL OR [Id] > @AfterId)
    ORDER BY [Id];
END;
