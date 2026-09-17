// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence.Migrations;

public partial class ConsolidateBetaDraftCommands : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => BetaDraftOrderSchema.Protect(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Beta purchasing command retirement requires forward correction or guarded recovery.', 1;");
}
