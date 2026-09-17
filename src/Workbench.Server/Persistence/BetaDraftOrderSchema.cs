// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static partial class BetaDraftOrderSchema
{
    internal static void Protect(MigrationBuilder migrationBuilder)
    {
        foreach (var suffix in new[] { "", "V2", "V3", "V4" })
            foreach (var operation in new[] { "Create", "Update", "Save" })
                migrationBuilder.Sql($"DROP PROCEDURE [Purchasing].[{operation}DraftOrder{suffix}];");

        // Fingerprint 4 and content schema 3 are retained storage formats, independent of the public beta lifecycle.
        migrationBuilder.Sql(SaveDraft);
        foreach (var operation in new[] { "Create", "Update" })
        {
            migrationBuilder.Sql($"""
                CREATE PROCEDURE [Purchasing].[{operation}DraftOrder]
                  @RequestId uniqueidentifier,@CanonicalInputJson nvarchar(max),@ActorUserId uniqueidentifier AS
                BEGIN
                  SET NOCOUNT ON;
                  EXEC [Purchasing].[SaveDraftOrder] @RequestId,@CanonicalInputJson,@ActorUserId,'{operation}';
                END;
                """);
            migrationBuilder.Sql($"GRANT EXECUTE ON [Purchasing].[{operation}DraftOrder] TO [workbench_web];");
        }
        migrationBuilder.Sql(ReplayReceipt);
        migrationBuilder.Sql("GRANT EXECUTE ON [Purchasing].[ReplayDraftOrderReceipt] TO [workbench_web];");
        migrationBuilder.Sql("""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260917010000_AddSupplierBasedDraftPricing',N'20260917080000_ConsolidateBetaDraftCommands');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    private const string ReplayReceipt = """
        CREATE PROCEDURE [Purchasing].[ReplayDraftOrderReceipt]
            @RequestId uniqueidentifier,@ActorUserId uniqueidentifier,@Operation varchar(6),
            @DraftOrderId uniqueidentifier,@ExpectedRowVersion varbinary(max),@FingerprintVersion int,@CanonicalInputJson nvarchar(max)
        AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
            IF @TenantId IS NULL OR NOT EXISTS(SELECT 1 FROM [Security].[fn_tenant_access](@TenantId))
                OR NOT EXISTS(SELECT 1 FROM [Identity].[Users] WHERE TenantId=@TenantId AND Id=@ActorUserId AND State=1)
                OR NOT EXISTS(SELECT 1 FROM [Tenancy].[Tenants] WHERE Id=@TenantId AND IsEnabled=1)
                THROW 50403,'Current tenant authority is required.',1;
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
                OR @Operation IS NULL OR CONVERT(varbinary(max),@Operation) NOT IN(CONVERT(varbinary(max),'Create'),CONVERT(varbinary(max),'Update'),CONVERT(varbinary(max),'Delete'))
                OR @FingerprintVersion IS NULL OR @FingerprintVersion NOT IN(1,2,3,4)
                OR @CanonicalInputJson IS NULL OR DATALENGTH(@CanonicalInputJson)>8388608 OR ISJSON(@CanonicalInputJson,OBJECT)<>1
                OR (@Operation='Create' AND (@DraftOrderId IS NOT NULL OR @ExpectedRowVersion IS NOT NULL))
                OR (@Operation IN('Update','Delete') AND (@DraftOrderId IS NULL OR @ExpectedRowVersion IS NULL OR DATALENGTH(@ExpectedRowVersion)<>8))
                THROW 50400,'A complete historical request is required.',1;
            -- These immutable receipts use the historical UTF-16 canonical bytes. Never normalize or re-fingerprint them.
            DECLARE @Fingerprint binary(32)=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@CanonicalInputJson));
            IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrderRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
                THROW 50427,'This API contract is unsupported. Reload the application before starting new work.',1;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrderRequestReceipts
                WHERE TenantId=@TenantId AND RequestId=@RequestId AND ActorUserId=@ActorUserId
                  AND Operation=@Operation AND FingerprintVersion=@FingerprintVersion AND InputFingerprint=@Fingerprint
                  AND ((@Operation='Create' AND ExpectedRowVersion IS NULL)
                    OR (DraftOrderId=@DraftOrderId AND ExpectedRowVersion=@ExpectedRowVersion)))
                THROW 50410,'This request identifier was used for different input.',1;
            SELECT RequestId,CONVERT(bit,1) Replayed,DraftOrderId,ResultRowVersion SavedVersion,CompletedAtUtc
                FROM Purchasing.DraftOrderRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
        END;
        """;
}
