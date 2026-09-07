// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionNotebook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Inventory");

            migrationBuilder.CreateTable(
                name: "Items",
                schema: "Inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TrackingKind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    StorageLocation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreationRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.UniqueConstraint("AK_Items_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Items_CreationRequestId", "[CreationRequestId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_Items_Name", "LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM [Name])) > 0");
                    table.CheckConstraint("CK_Items_TrackingKind", "[TrackingKind] = 'Individual'");
                    table.ForeignKey(
                        name: "FK_Items_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Items_TenantId_CreatedAtUtc_Id",
                schema: "Inventory",
                table: "Items",
                columns: new[] { "TenantId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_TenantId_CreationRequestId",
                schema: "Inventory",
                table: "Items",
                columns: new[] { "TenantId", "CreationRequestId" },
                unique: true);
            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[Items],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[Items] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[Items] AFTER UPDATE;
                GRANT SELECT, INSERT ON [Inventory].[Items] TO [workbench_web];
                DENY UPDATE, DELETE ON [Inventory].[Items] TO [workbench_web];
                DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
                SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
                SET @Readiness = REPLACE(@Readiness, N'20260906092000_DeferInvitationIdentityClaim', N'20260907043931_AddCollectionNotebook');
                EXEC sys.sp_executesql @Readiness;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Collection records cannot be dropped safely; use a reviewed forward migration or offline recovery.', 1;");
        }
    }
}
