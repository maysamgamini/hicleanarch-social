using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Timeline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FanoutRemovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_fanout_jobs",
                schema: "timeline",
                table: "fanout_jobs");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "timeline",
                table: "fanout_jobs",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "ADD");

            migrationBuilder.AddPrimaryKey(
                name: "pk_fanout_jobs",
                schema: "timeline",
                table: "fanout_jobs",
                columns: new[] { "post_id", "kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_fanout_jobs",
                schema: "timeline",
                table: "fanout_jobs");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "timeline",
                table: "fanout_jobs");

            migrationBuilder.AddPrimaryKey(
                name: "pk_fanout_jobs",
                schema: "timeline",
                table: "fanout_jobs",
                column: "post_id");
        }
    }
}
