using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Timeline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "timeline");

            migrationBuilder.CreateTable(
                name: "fanout_progress",
                schema: "timeline",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    followers_reached = table.Column<long>(type: "bigint", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fanout_progress", x => x.post_id);
                });

            migrationBuilder.CreateIndex(
                name: "fanout_progress_open",
                schema: "timeline",
                table: "fanout_progress",
                column: "stored_at",
                filter: "completed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fanout_progress",
                schema: "timeline");
        }
    }
}
