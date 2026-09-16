// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static partial class SupplierPricingDraftOrderSchema
{
    internal static void Protect(MigrationBuilder migrationBuilder, string migrationId)
    {
        migrationBuilder.Sql(SaveDraftV4);
        foreach (var operation in new[] { "Create", "Update" })
        {
            migrationBuilder.Sql($"""
                CREATE PROCEDURE [Purchasing].[{operation}DraftOrderV4]
                  @RequestId uniqueidentifier,@CanonicalInputJson nvarchar(max),@ActorUserId uniqueidentifier AS
                BEGIN
                  SET NOCOUNT ON;
                  EXEC [Purchasing].[SaveDraftOrderV4] @RequestId,@CanonicalInputJson,@ActorUserId,'{operation}';
                END;
                """);
            migrationBuilder.Sql($"GRANT EXECUTE ON [Purchasing].[{operation}DraftOrderV4] TO [workbench_web];");
        }
        migrationBuilder.Sql("""
            DECLARE @Old nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[SaveDraftOrderV3]'));
            SET @Old=REPLACE(@Old,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Old=REPLACE(@Old,N'IF @CurrentVersion<>@ExpectedVersion',N'IF @Operation=''Update'' AND EXISTS(SELECT 1 FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId AND ContentSchemaVersion=3) THROW 50426,''Reload this draft with the current contract.'',1; IF @CurrentVersion<>@ExpectedVersion');
            EXEC sys.sp_executesql @Old;
            """);
        migrationBuilder.Sql($"""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260916183834_AddStructuredDraftOrderLines',N'{migrationId}');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
}
