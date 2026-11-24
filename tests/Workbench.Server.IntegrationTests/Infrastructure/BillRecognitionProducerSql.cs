// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class BillRecognitionProducerSql
{
    // This is the existing durable fixture source producer's correction contract.
    // It validates actual published source rows/heads; it never fabricates financial history.
    internal const string Validator = """
        CREATE PROCEDURE Purchasing.ValidateFixtureRecognitionOwner
          @TenantId uniqueidentifier,@EventId uniqueidentifier,@Replacement nvarchar(max),@Facts nvarchar(max) OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
            OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
            THROW 51009,'Fixture producer validation requires the actual source transaction.',1;
          DECLARE @Source uniqueidentifier,@OriginalRevision uniqueidentifier,@Head uniqueidentifier,@Component nvarchar(200),
            @Evidence nvarchar(max),@OriginalEvidence nvarchar(max),@SuccessorRevision uniqueidentifier,
            @Side nvarchar(max),@SourceAmount decimal(28,4),@Quantity decimal(28,6),@Class varchar(16);
          SELECT @Source=actual.SourceId,@OriginalRevision=actual.SourceRevision,@Component=actual.SourceComponentKey,
              @Head=head.CurrentRevision,@OriginalEvidence=producer.EvidenceJson,@SourceAmount=actual.SourceAmount,
              @Quantity=actual.SourceQuantity,@Class=unit.Classification
            FROM Purchasing.RecognitionSideEvents actual
            JOIN Purchasing.RecognitionUnits unit ON unit.TenantId=actual.TenantId AND unit.Id=actual.UnitId
            JOIN Purchasing.FixtureRecognitionSources producer WITH(UPDLOCK,HOLDLOCK)
              ON producer.TenantId=actual.TenantId AND producer.Id=actual.SourceId AND producer.Revision=actual.SourceRevision
                AND producer.SourceComponentKey=actual.SourceComponentKey AND producer.Side=actual.Side
                AND producer.PurchaseOrderId=unit.PurchaseOrderId AND producer.SupplierId=unit.SupplierId
                AND producer.Currency=unit.Currency AND producer.Classification=unit.Classification
            JOIN Purchasing.FixtureRecognitionSourceHeads head WITH(UPDLOCK,HOLDLOCK)
              ON head.TenantId=producer.TenantId AND head.SourceId=producer.Id AND head.Side=producer.Side
            WHERE actual.TenantId=@TenantId AND actual.Id=@EventId AND actual.Side='Recognition'
              AND actual.EvidenceMutationPermission='PurchaseRecognitionFixturePost'
              AND producer.CorrectionAllowed=1 AND producer.DependencyKind IS NULL;
          IF @Source IS NULL THROW 51004,'The current producer cannot correct this source or its downstream dependency.',1;
          IF @Replacement IS NOT NULL
          BEGIN
            SET @SuccessorRevision=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Replacement,'$.sourceRevision'));
            IF @Head<>@SuccessorRevision OR @SuccessorRevision=@OriginalRevision
              THROW 51009,'The current published upstream revision changed.',1;
            SELECT @Evidence=producer.EvidenceJson FROM Purchasing.FixtureRecognitionSources producer WITH(UPDLOCK,HOLDLOCK)
              JOIN Purchasing.RecognitionSideEvents original ON original.TenantId=producer.TenantId AND original.Id=@EventId
              JOIN Purchasing.RecognitionUnits unit ON unit.TenantId=original.TenantId AND unit.Id=original.UnitId
              WHERE producer.TenantId=@TenantId AND producer.Id=@Source AND producer.Revision=@SuccessorRevision
                AND producer.SourceComponentKey=@Component AND producer.Side='Recognition' AND producer.CorrectionAllowed=1
                AND producer.DependencyKind IS NULL AND producer.Classification=@Class
                AND producer.PurchaseOrderId=unit.PurchaseOrderId AND producer.SupplierId=unit.SupplierId AND producer.Currency=unit.Currency
                AND TRY_CONVERT(decimal(28,6),JSON_VALUE(producer.EvidenceJson,'$.sourceCapacityQuantity'))=@Quantity
                AND TRY_CONVERT(decimal(28,4),JSON_VALUE(producer.EvidenceJson,'$.sourceCapacityAmount'))=@SourceAmount;
            IF @Evidence IS NULL THROW 51004,'The producer has no eligible complete published successor.',1;
            SELECT @Side=(SELECT actual.Side side,actual.EventRevision+1 eventRevision,actual.SourceId sourceId,
                @SuccessorRevision sourceRevision,actual.SourceComponentKey sourceComponentKey,actual.SubdivisionKey subdivisionKey,
                CONVERT(nvarchar(60),actual.SourceQuantity) sourceQuantity,CONVERT(nvarchar(60),actual.SourceAmount) sourceAmount,
                actual.DocumentDate documentDate,actual.EffectiveDate effectiveDate,JSON_QUERY(@Evidence) evidence,
                JSON_QUERY((SELECT ComponentKey componentKey,Kind kind,CONVERT(nvarchar(60),Amount) amount,Reason reason,AssignedCostComponentKey assignedCostComponentKey
                  FROM Purchasing.RecognitionComponents component WHERE component.TenantId=actual.TenantId AND component.EventId=actual.Id
                  ORDER BY ComponentKey FOR JSON PATH)) components
                FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
              FROM Purchasing.RecognitionSideEvents actual WHERE actual.TenantId=@TenantId AND actual.Id=@EventId;
          END;
          SET @Facts=(SELECT @Source sourceId,@OriginalRevision originalRevision,@Head currentRevision,@Class classification,
            CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@OriginalEvidence)),2) originalEvidenceSha256,
            JSON_QUERY(@Side) successorSide FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
        END;
        """;

    internal const string Register = """
        INSERT Purchasing.RecognitionSourceOwners(PostingPermission,CorrectionPermission,ValidatorSchema,ValidatorName,DefinitionSha256)
          VALUES(N'PurchaseRecognitionFixturePost',N'PurchaseRecognitionFixtureCorrect',N'Purchasing',N'ValidateFixtureRecognitionOwner',
            HASHBYTES('SHA2_256',CONVERT(varbinary(max),OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.ValidateFixtureRecognitionOwner')))));
        DENY EXECUTE ON Purchasing.ValidateFixtureRecognitionOwner TO workbench_web;
        DENY EXECUTE ON Purchasing.ValidateFixtureRecognitionOwner TO workbench_worker;
        """;
}
