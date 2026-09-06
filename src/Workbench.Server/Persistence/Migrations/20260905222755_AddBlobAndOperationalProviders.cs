// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBlobAndOperationalProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Storage");

            migrationBuilder.CreateTable(
                name: "Attachments",
                schema: "Storage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeleteAfterUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Held = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.UniqueConstraint("AK_Attachments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Attachments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Storage].[Attachments],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Storage].[Attachments] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Storage].[Attachments] AFTER UPDATE;
                GRANT SELECT, INSERT, UPDATE ON [Storage].[Attachments] TO [workbench_web];
                DENY DELETE ON [Storage].[Attachments] TO [workbench_web];
                """);
            migrationBuilder.CreateTable(
                name: "Revisions",
                schema: "Storage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderAlias = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MediaType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Revisions", x => x.Id);
                    table.UniqueConstraint("AK_Revisions_TenantId_AttachmentId_Id", x => new { x.TenantId, x.AttachmentId, x.Id });
                    table.UniqueConstraint("AK_Revisions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Revisions_Content", "([State] IN (0,2) AND [Length] IS NULL AND [Sha256] IS NULL) OR ([State] IN (1,3) AND [Length] IS NOT NULL AND [Sha256] IS NOT NULL AND [Length] >= 0 AND LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9A-F]%')");
                    table.ForeignKey(
                        name: "FK_Revisions_Attachments_TenantId_AttachmentId",
                        columns: x => new { x.TenantId, x.AttachmentId },
                        principalSchema: "Storage",
                        principalTable: "Attachments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Revisions_Revisions_TenantId_AttachmentId_PreviousRevisionId",
                        columns: x => new { x.TenantId, x.AttachmentId, x.PreviousRevisionId },
                        principalSchema: "Storage",
                        principalTable: "Revisions",
                        principalColumns: new[] { "TenantId", "AttachmentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_TenantId_Id_CurrentRevisionId",
                schema: "Storage",
                table: "Attachments",
                columns: new[] { "TenantId", "Id", "CurrentRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Revisions_TenantId_AttachmentId_PreviousRevisionId",
                schema: "Storage",
                table: "Revisions",
                columns: new[] { "TenantId", "AttachmentId", "PreviousRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Revisions_TenantId_OperationId",
                schema: "Storage",
                table: "Revisions",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_Revisions_TenantId_Id_CurrentRevisionId",
                schema: "Storage",
                table: "Attachments",
                columns: new[] { "TenantId", "Id", "CurrentRevisionId" },
                principalSchema: "Storage",
                principalTable: "Revisions",
                principalColumns: new[] { "TenantId", "AttachmentId", "Id" },
                onDelete: ReferentialAction.Restrict);

            ProtectRevisions(migrationBuilder);
            migrationBuilder.EnsureSchema(
                name: "Operations");

            migrationBuilder.CreateTable(
                name: "WorkItems",
                schema: "Operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdentityOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProtectedPayload = table.Column<byte[]>(type: "varbinary(8000)", maxLength: 8000, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Generation = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LeaseOwner = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.Id);
                    table.UniqueConstraint("AK_WorkItems_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_WorkItems_Kind", "([Kind] = 1 AND [AttachmentId] IS NOT NULL AND [IdentityOperationId] IS NULL AND [ProtectedPayload] IS NULL) OR ([Kind] = 2 AND [IdentityOperationId] IS NOT NULL AND [AttachmentId] IS NULL)");
                    table.CheckConstraint("CK_WorkItems_State", "[State] BETWEEN 0 AND 3 AND [Attempts] BETWEEN 0 AND 5 AND [Generation] >= 0");
                    table.ForeignKey(
                        name: "FK_WorkItems_Attachments_TenantId_AttachmentId",
                        columns: x => new { x.TenantId, x.AttachmentId },
                        principalSchema: "Storage",
                        principalTable: "Attachments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItems_IdentityOperations_TenantId_IdentityOperationId",
                        columns: x => new { x.TenantId, x.IdentityOperationId },
                        principalSchema: "Identity",
                        principalTable: "IdentityOperations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_State_AvailableAtUtc",
                schema: "Operations",
                table: "WorkItems",
                columns: new[] { "State", "AvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_TenantId_AttachmentId",
                schema: "Operations",
                table: "WorkItems",
                columns: new[] { "TenantId", "AttachmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_TenantId_IdentityOperationId",
                schema: "Operations",
                table: "WorkItems",
                columns: new[] { "TenantId", "IdentityOperationId" });
            OperationalSchema.CreateQueueProcedures(migrationBuilder);
        }

        private static void ProtectRevisions(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Storage].[Revisions],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Storage].[Revisions] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Storage].[Revisions] AFTER UPDATE;
                GRANT SELECT, INSERT, UPDATE ON [Storage].[Revisions] TO [workbench_web];
                DENY DELETE ON [Storage].[Revisions] TO [workbench_web];
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER [Storage].[ProtectRevision] ON [Storage].[Revisions] AFTER UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF UPDATE([TenantId]) OR UPDATE([Id]) OR UPDATE([AttachmentId]) OR UPDATE([OperationId])
                        OR UPDATE([ActorUserId]) OR UPDATE([PreviousRevisionId]) OR UPDATE([Source])
                        OR UPDATE([CreatedAtUtc])
                        THROW 50021, 'Revision provenance is immutable.', 1;
                    IF EXISTS (SELECT 1 FROM deleted WHERE [State] IN (1,3)) AND
                        (UPDATE([Length]) OR UPDATE([Sha256]) OR UPDATE([MediaType]))
                        THROW 50021, 'Published content identity is immutable.', 1;
                    IF UPDATE([ProviderAlias]) AND IS_ROLEMEMBER(N'workbench_migrator') <> 1 AND IS_MEMBER(N'db_owner') <> 1
                        THROW 50021, 'Provider migration requires maintenance authority.', 1;
                    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.[Id] = d.[Id]
                        WHERE NOT ((d.[State] = 0 AND i.[State] IN (0,1,2)) OR
                                   (d.[State] = 1 AND i.[State] IN (1,3)) OR
                                   (d.[State] = 2 AND i.[State] = 2) OR
                                   (d.[State] = 3 AND i.[State] = 3)))
                        THROW 50021, 'Invalid revision transition.', 1;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Blob and operational metadata rollback requires an explicit offline recovery procedure.', 1;");
    }
}
