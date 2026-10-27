using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recommendations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Dismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "recommendations");

            migrationBuilder.CreateTable(
                name: "dismissals",
                schema: "recommendations",
                columns: table => new
                {
                    reader_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dismissals", x => new { x.reader_id, x.account_id });
                });

            migrationBuilder.CreateIndex(
                name: "dismissals_by_until",
                schema: "recommendations",
                table: "dismissals",
                column: "until");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dismissals",
                schema: "recommendations");
        }
    }
}
