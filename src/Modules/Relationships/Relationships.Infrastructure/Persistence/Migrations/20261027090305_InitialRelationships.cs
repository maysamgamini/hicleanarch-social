using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relationships.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "relationships");

            migrationBuilder.CreateTable(
                name: "follows",
                schema: "relationships",
                columns: table => new
                {
                    follower_id = table.Column<Guid>(type: "uuid", nullable: false),
                    followee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_follows", x => new { x.follower_id, x.followee_id });
                    table.CheckConstraint("follows_not_self", "follower_id <> followee_id");
                });

            migrationBuilder.CreateTable(
                name: "pair_versions",
                schema: "relationships",
                columns: table => new
                {
                    low_id = table.Column<Guid>(type: "uuid", nullable: false),
                    high_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pair_versions", x => new { x.low_id, x.high_id });
                    table.CheckConstraint("pair_versions_ordered", "low_id < high_id");
                });

            migrationBuilder.CreateTable(
                name: "relationship_counts",
                schema: "relationships",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    followers = table.Column<long>(type: "bigint", nullable: false),
                    following = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relationship_counts", x => x.account_id);
                });

            migrationBuilder.CreateIndex(
                name: "follows_by_followee",
                schema: "relationships",
                table: "follows",
                columns: new[] { "followee_id", "follower_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "follows",
                schema: "relationships");

            migrationBuilder.DropTable(
                name: "pair_versions",
                schema: "relationships");

            migrationBuilder.DropTable(
                name: "relationship_counts",
                schema: "relationships");
        }
    }
}
