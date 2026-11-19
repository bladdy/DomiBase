using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DomiBase.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeyRotationAndExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "api_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "previous_hash_expires_at",
                table: "api_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "previous_secret_hash",
                table: "api_keys",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_previous_secret_hash",
                table: "api_keys",
                column: "previous_secret_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_api_keys_previous_secret_hash",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "previous_hash_expires_at",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "previous_secret_hash",
                table: "api_keys");
        }
    }
}
