-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanSupplierCreditEvidence
  @TenantId uniqueidentifier,@Sources nvarchar(max),@Dependencies nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Evidence planning requires its source owner transaction.',1;
  DECLARE @Roots TABLE(SetId uniqueidentifier PRIMARY KEY,Po uniqueidentifier,Supplier uniqueidentifier,Currency varchar(3));
  INSERT @Roots SELECT evidence.Id,credit.PurchaseOrderId,credit.SupplierId,credit.Currency
    FROM OPENJSON(@Sources) source JOIN Purchasing.SupplierCredits credit ON credit.TenantId=@TenantId
      AND credit.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))
    JOIN Accounting.FinancialEvidenceSets evidence WITH(UPDLOCK,HOLDLOCK) ON evidence.TenantId=credit.TenantId
      AND evidence.OwnerKind='SupplierCredit' AND evidence.OwnerId=credit.Id AND evidence.OwnerRevisionId=credit.RevisionId
      AND evidence.PurchaseOrderId=credit.PurchaseOrderId AND evidence.SupplierId=credit.SupplierId
      AND evidence.ActorId=credit.ActorId AND evidence.RecordedAtUtc=credit.RecordedAtUtc
      AND evidence.SourceSnapshotSha256=HASHBYTES('SHA2_256',CONVERT(varbinary(max),credit.SourceSnapshotJson));
  IF (SELECT COUNT(*) FROM @Roots)<>(SELECT COUNT(*) FROM OPENJSON(@Sources))
    THROW 51004,'Authentic credit evidence ownership is incomplete.',1;
  DECLARE @Sets TABLE(Id uniqueidentifier PRIMARY KEY,ParentId uniqueidentifier,Version varchar(18));
  DECLARE @Visited TABLE(Id uniqueidentifier PRIMARY KEY);
  DECLARE @Set uniqueidentifier,@Parent uniqueidentifier,@Version varchar(18),@Po uniqueidentifier,@Supplier uniqueidentifier,@Currency varchar(3);
  DECLARE roots CURSOR LOCAL FAST_FORWARD FOR SELECT SetId,Po,Supplier,Currency FROM @Roots ORDER BY SetId;
  OPEN roots; FETCH NEXT FROM roots INTO @Set,@Po,@Supplier,@Currency;
  WHILE @@FETCH_STATUS=0
  BEGIN
    DELETE @Visited;
    WHILE @Set IS NOT NULL
    BEGIN
      IF EXISTS(SELECT 1 FROM @Visited WHERE Id=@Set) THROW 51004,'An inherited evidence cycle has no supported owner.',1;
      INSERT @Visited VALUES(@Set);
      SET @Version=NULL; SET @Parent=NULL;
      SELECT @Parent=evidence.InheritedEvidenceSetId,@Version=CONVERT(varchar(18),CONVERT(binary(8),evidence.RowVersion),1)
        FROM Accounting.FinancialEvidenceSets evidence WITH(UPDLOCK,HOLDLOCK)
        JOIN Purchasing.SupplierCredits credit ON credit.TenantId=evidence.TenantId AND credit.Id=evidence.OwnerId
          AND credit.RevisionId=evidence.OwnerRevisionId AND credit.PurchaseOrderId=@Po AND credit.SupplierId=@Supplier AND credit.Currency=@Currency
          AND credit.ActorId=evidence.ActorId AND credit.RecordedAtUtc=evidence.RecordedAtUtc
          AND evidence.SourceSnapshotSha256=HASHBYTES('SHA2_256',CONVERT(varbinary(max),credit.SourceSnapshotJson))
        WHERE evidence.TenantId=@TenantId AND evidence.Id=@Set AND evidence.OwnerKind='SupplierCredit'
          AND evidence.PurchaseOrderId=@Po AND evidence.SupplierId=@Supplier;
      IF @Version IS NULL THROW 51004,'An inherited credit evidence owner is unavailable.',1;
      IF NOT EXISTS(SELECT 1 FROM @Sets WHERE Id=@Set) INSERT @Sets VALUES(@Set,@Parent,@Version);
      IF (SELECT COUNT(*) FROM @Sets)>1000 THROW 51000,'The inherited evidence graph exceeds its event bound.',1;
      SET @Set=@Parent;
    END;
    FETCH NEXT FROM roots INTO @Set,@Po,@Supplier,@Currency;
  END;
  CLOSE roots; DEALLOCATE roots;
  DECLARE @Nodes TABLE(Kind varchar(40),Id uniqueidentifier,Version varchar(66),ParentId uniqueidentifier);
  INSERT @Nodes SELECT 'FinancialEvidenceSet',Id,Version,ParentId FROM @Sets;
  INSERT @Nodes SELECT 'FinancialEvidenceLink',link.Id,link.Sha256,link.EvidenceSetId
    FROM Accounting.FinancialEvidenceLinks link JOIN @Sets evidence ON evidence.Id=link.EvidenceSetId WHERE link.TenantId=@TenantId;
  INSERT @Nodes SELECT 'FinancialEvidenceAddition',addition.Id,NULL,addition.EvidenceSetId
    FROM Accounting.FinancialEvidenceAdditions addition JOIN @Sets evidence ON evidence.Id=addition.EvidenceSetId WHERE addition.TenantId=@TenantId;
  IF (SELECT COUNT(*) FROM @Nodes)>1000 THROW 51000,'The complete inherited evidence closure exceeds its event bound.',1;
  SET @Dependencies=(SELECT Kind kind,Id id,Version version,ParentId parentId FROM @Nodes
    ORDER BY Kind,Id,ParentId,Version FOR JSON PATH,INCLUDE_NULL_VALUES);
END;
