using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DomiBase.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectRateLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rate_limit_permits",
                table: "projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rate_limit_window_seconds",
                table: "projects",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rate_limit_permits",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "rate_limit_window_seconds",
                table: "projects");
        }
    }
}
