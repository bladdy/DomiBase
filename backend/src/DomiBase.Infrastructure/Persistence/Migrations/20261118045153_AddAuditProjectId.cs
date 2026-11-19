using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DomiBase.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditProjectId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "audit_entries",
                type: "uuid",
                nullable: true);

            // Backfill historical rows. Project-targeted events use target_id as the project
            // id; key events carry ProjectId in their JSON details. Both casts are guarded by
            // a UUID regex so malformed legacy data can never break the migration.
            const string uuidPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";

            migrationBuilder.Sql($$"""
                UPDATE audit_entries
                SET project_id = target_id::uuid
                WHERE project_id IS NULL
                  AND target_type = 'project'
                  AND target_id ~ '{{uuidPattern}}';

                UPDATE audit_entries
                SET project_id = (details ->> 'ProjectId')::uuid
                WHERE project_id IS NULL
                  AND details IS NOT NULL
                  AND details ->> 'ProjectId' ~ '{{uuidPattern}}';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_project_id",
                table: "audit_entries",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_audit_entries_project_id",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "audit_entries");
        }
    }
}
