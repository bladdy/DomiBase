using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DomiBase.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "runtime_password_ciphertext",
                table: "projects",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "runtime_password_ciphertext",
                table: "projects");
        }
    }
}
