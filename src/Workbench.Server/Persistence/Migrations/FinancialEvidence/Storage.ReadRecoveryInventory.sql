-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Storage].[ReadRecoveryInventory] @Inventory nvarchar(max) = NULL OUTPUT
WITH EXECUTE AS OWNER AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Generation bigint;
    SELECT @Generation=RestoreGeneration FROM [Security].[DatabaseSecurityState] WITH (HOLDLOCK)
        WHERE Id=1 AND RestoreGeneration>0 AND RestoreGeneration=RestoreSanitizedGeneration;
    IF @Generation IS NULL OR EXISTS(SELECT 1 FROM [Security].[WorkbenchRestorePending] WHERE IsPending=1)
        OR NOT EXISTS(SELECT 1 FROM [Security].[BlobRecoveryState] WHERE Id=1 AND IsPending=1)
        THROW 50043, 'Sanitized isolated recovery is required.', 1;
    DECLARE @Evidence nvarchar(max)=(SELECT
  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceSets WITH(HOLDLOCK) ORDER BY TenantId,Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Sets,
  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceLinks WITH(HOLDLOCK) ORDER BY TenantId,Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Links,
  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceAdditions WITH(HOLDLOCK) ORDER BY TenantId,Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Additions,
  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceReceipts WITH(HOLDLOCK) ORDER BY TenantId,RequestId FOR JSON PATH,INCLUDE_NULL_VALUES)) Receipts,
  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceDisposals WITH(HOLDLOCK) ORDER BY TenantId,RequestId FOR JSON PATH,INCLUDE_NULL_VALUES)) Disposals,
  JSON_QUERY((SELECT * FROM Accounting.FinancialEvidenceDisposalLinks WITH(HOLDLOCK) ORDER BY TenantId,RequestId,LinkId FOR JSON PATH,INCLUDE_NULL_VALUES)) DisposalLinks,
  JSON_QUERY((SELECT * FROM Storage.FinancialEvidenceAttachmentStates WITH(HOLDLOCK) ORDER BY TenantId,AttachmentId FOR JSON PATH)) AttachmentVersions,
  JSON_QUERY((SELECT a.TenantId,a.Id,a.Held,a.IndependentHeld,a.CurrentRevisionId,a.DeletedAtUtc,a.DeleteAfterUtc
    FROM Storage.Attachments a WITH(HOLDLOCK) WHERE Storage.FinanciallyProtectedRevision(a.TenantId,a.Id)=1
    ORDER BY a.TenantId,a.Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Attachments,
  JSON_QUERY((SELECT d.TenantId,d.Id,d.AttachmentId,d.RevisionId,d.RemovedAtUtc
    FROM Purchasing.PurchaseOrderDocuments d WITH(HOLDLOCK) WHERE Storage.FinanciallyProtectedRevision(d.TenantId,d.AttachmentId)=1
    ORDER BY d.TenantId,d.Id FOR JSON PATH,INCLUDE_NULL_VALUES)) Documents
  FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
DECLARE @EvidenceFingerprint varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@Evidence),2);
DECLARE @Rows nvarchar(max) = (SELECT TenantId, Id AS RevisionId, ProviderAlias,
        [Length], Sha256, [State], RowVersion,
Storage.FinanciallyProtectedRevision(TenantId,AttachmentId) FinanciallyProtected,
CONVERT(bit,CASE WHEN Storage.FinanciallyProtectedRevision(TenantId,AttachmentId)=1
  AND Storage.FinancialEvidenceDeletionAuthorized(TenantId,AttachmentId)=1 THEN 1 ELSE 0 END) Disposed FROM [Storage].[Revisions] WITH (HOLDLOCK)
        ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES);
    SET @Inventory=(SELECT DB_NAME() AS [Database], CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) AS [Server],
        @Generation AS Generation, @EvidenceFingerprint AS FinancialEvidenceFingerprint, JSON_QUERY(@Rows) AS [Rows] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
END;
