using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Timeline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RebuildJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rebuild_jobs",
                schema: "timeline",
                columns: table => new
                {
                    reader_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rebuild_jobs", x => x.reader_id);
                });

            migrationBuilder.CreateIndex(
                name: "rebuild_jobs_oldest",
                schema: "timeline",
                table: "rebuild_jobs",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rebuild_jobs",
                schema: "timeline");
        }
    }
}
