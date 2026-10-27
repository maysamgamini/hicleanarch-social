using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Timeline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FanoutJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fanout_jobs",
                schema: "timeline",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audience = table.Column<long>(type: "bigint", nullable: false),
                    pool = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    cursor = table.Column<Guid>(type: "uuid", nullable: true),
                    author_done = table.Column<bool>(type: "boolean", nullable: false),
                    followers_reached = table.Column<long>(type: "bigint", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fanout_jobs", x => x.post_id);
                });

            migrationBuilder.CreateIndex(
                name: "fanout_jobs_waiting",
                schema: "timeline",
                table: "fanout_jobs",
                columns: new[] { "pool", "created_at" },
                filter: "status <> 'DONE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fanout_jobs",
                schema: "timeline");
        }
    }
}
