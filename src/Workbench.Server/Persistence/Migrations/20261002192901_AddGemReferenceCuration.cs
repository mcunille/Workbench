// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGemReferenceCuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            GemReferenceCurationSchema.Up(migrationBuilder, "20261002192901_AddGemReferenceCuration");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020,'Catalog curation rollback is blocked to preserve drafts and publication evidence.',1;");
        }
    }
}
