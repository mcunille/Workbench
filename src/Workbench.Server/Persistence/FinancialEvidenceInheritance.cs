// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceInheritance
{
    internal const string LinksSql = """
        CREATE FUNCTION Accounting.InheritedFinancialEvidence(@TenantId uniqueidentifier,@EvidenceSetId uniqueidentifier)
        RETURNS @Links TABLE(DocumentId uniqueidentifier,RevisionId uniqueidentifier,AttachmentId uniqueidentifier,Sha256 varchar(64),Length bigint,Label nvarchar(200),PRIMARY KEY(DocumentId,RevisionId))
        AS BEGIN
          DECLARE @Visited TABLE(Id uniqueidentifier PRIMARY KEY);
          DECLARE @Po uniqueidentifier=(SELECT PurchaseOrderId FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@EvidenceSetId);
          WHILE @EvidenceSetId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM @Visited WHERE Id=@EvidenceSetId)
          BEGIN
            INSERT @Visited VALUES(@EvidenceSetId);
            INSERT @Links SELECT l.DocumentId,l.RevisionId,l.AttachmentId,l.Sha256,l.Length,l.Label
              FROM Accounting.FinancialEvidenceLinks l JOIN Accounting.FinancialEvidenceSets s ON s.TenantId=l.TenantId AND s.Id=l.EvidenceSetId
              WHERE l.TenantId=@TenantId AND l.EvidenceSetId=@EvidenceSetId AND s.PurchaseOrderId=@Po
                AND NOT EXISTS(SELECT 1 FROM @Links old WHERE old.DocumentId=l.DocumentId AND old.RevisionId=l.RevisionId);
            SET @EvidenceSetId=(SELECT InheritedEvidenceSetId FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@EvidenceSetId AND PurchaseOrderId=@Po);
          END;
          RETURN;
        END;
        """;

    internal const string ValidateSql = """
        CREATE PROCEDURE Accounting.ValidateInheritedFinancialEvidence @TenantId uniqueidentifier,@Po uniqueidentifier,@Payload nvarchar(max),
          @Inherited uniqueidentifier,@Evidence nvarchar(max) OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 THROW 51000,'Source transaction required.',1;
          IF @Inherited IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Accounting.FinancialEvidenceSets WHERE TenantId=@TenantId AND Id=@Inherited AND PurchaseOrderId=@Po)
            THROW 51004,'Evidence ancestor unavailable.',1;
          IF EXISTS(SELECT JSON_VALUE(value,'$.documentId') FROM OPENJSON(@Payload,'$.documents') GROUP BY JSON_VALUE(value,'$.documentId') HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.documents') WHERE type<>5 OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.documentId')) IS NULL
              OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.revisionId')) IS NULL) THROW 51000,'Invalid evidence document identities.',1;
          DECLARE @Original TABLE(DocumentId uniqueidentifier,RevisionId uniqueidentifier,AttachmentId uniqueidentifier,Sha256 varchar(64),Length bigint,Label nvarchar(200),PRIMARY KEY(DocumentId,RevisionId));
          INSERT @Original SELECT l.* FROM Accounting.InheritedFinancialEvidence(@TenantId,@Inherited) l JOIN OPENJSON(@Payload,'$.documents') j
            ON l.DocumentId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId')) AND l.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId'));
          IF EXISTS(SELECT 1 FROM @Original l WHERE NOT EXISTS(SELECT 1 FROM Storage.Revisions r
                JOIN Purchasing.PurchaseOrderDocuments d ON d.TenantId=r.TenantId AND d.AttachmentId=r.AttachmentId AND d.RevisionId=r.Id
                WHERE r.TenantId=@TenantId AND r.Id=l.RevisionId AND r.AttachmentId=l.AttachmentId AND r.Sha256=l.Sha256 AND r.Length=l.Length
                  AND d.Id=l.DocumentId AND d.OrderId=@Po AND d.Sha256=l.Sha256 AND d.Length=l.Length))
            OR EXISTS(SELECT 1 FROM @Original l JOIN OPENJSON(@Payload,'$.documents') j ON l.DocumentId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId'))
              WHERE (JSON_VALUE(j.value,'$.digest') IS NOT NULL AND JSON_VALUE(j.value,'$.digest')<>l.Sha256)
                OR (JSON_VALUE(j.value,'$.length') IS NOT NULL AND (TRY_CONVERT(bigint,JSON_VALUE(j.value,'$.length')) IS NULL OR TRY_CONVERT(bigint,JSON_VALUE(j.value,'$.length'))<>l.Length)))
            THROW 51004,'Inherited evidence identity is inconsistent.',1;
          DECLARE @NewPayload nvarchar(max)=(SELECT JSON_QUERY(COALESCE(N'['+(SELECT STRING_AGG(CONVERT(nvarchar(max),j.value),N',') FROM OPENJSON(@Payload,'$.documents') j
            WHERE NOT EXISTS(SELECT 1 FROM @Original l WHERE l.DocumentId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.documentId')) AND l.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(j.value,'$.revisionId'))))+N']',N'[]')) documents FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),@New nvarchar(max);
          EXEC Purchasing.ValidateBillEvidence @TenantId,@Po,@NewPayload,@New OUTPUT;
          SET @Evidence=(SELECT documentId,revisionId,digest,length,label FROM (
            SELECT DocumentId documentId,RevisionId revisionId,Sha256 digest,Length length,Label label FROM @Original
            UNION ALL SELECT documentId,revisionId,digest,length,label FROM OPENJSON(@New) WITH(documentId uniqueidentifier,revisionId uniqueidentifier,digest varchar(64),length bigint,label nvarchar(200))) allDocs
            ORDER BY documentId FOR JSON PATH);
        END;
        """;
}
