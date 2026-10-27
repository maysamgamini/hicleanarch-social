using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relationships.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BlocksAndMutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocks",
                schema: "relationships",
                columns: table => new
                {
                    blocker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocks", x => new { x.blocker_id, x.blocked_id });
                    table.CheckConstraint("blocks_not_self", "blocker_id <> blocked_id");
                });

            migrationBuilder.CreateTable(
                name: "mutes",
                schema: "relationships",
                columns: table => new
                {
                    muter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    muted_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mutes", x => new { x.muter_id, x.muted_id });
                    table.CheckConstraint("mutes_not_self", "muter_id <> muted_id");
                });

            migrationBuilder.CreateIndex(
                name: "blocks_by_blocked",
                schema: "relationships",
                table: "blocks",
                column: "blocked_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocks",
                schema: "relationships");

            migrationBuilder.DropTable(
                name: "mutes",
                schema: "relationships");
        }
    }
}
