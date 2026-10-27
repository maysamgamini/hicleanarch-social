using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recommendations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "recommendations");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "recommendations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    followers = table.Column<long>(type: "bigint", nullable: false),
                    following = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hidden_edges",
                schema: "recommendations",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    v = table.Column<long>(type: "bigint", nullable: false),
                    on = table.Column<bool>(type: "boolean", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hidden_edges", x => new { x.owner_id, x.target_id, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "follow_edges",
                schema: "recommendations",
                columns: table => new
                {
                    follower_id = table.Column<Guid>(type: "uuid", nullable: false),
                    followee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    v = table.Column<long>(type: "bigint", nullable: false),
                    on = table.Column<bool>(type: "boolean", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_follow_edges", x => new { x.follower_id, x.followee_id });
                    table.ForeignKey(
                        name: "fk_follow_edges_accounts_followee_id",
                        column: x => x.followee_id,
                        principalSchema: "recommendations",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_follow_edges_accounts_follower_id",
                        column: x => x.follower_id,
                        principalSchema: "recommendations",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "follow_edges_newest",
                schema: "recommendations",
                table: "follow_edges",
                columns: new[] { "follower_id", "since" },
                descending: new[] { false, true },
                filter: "\"on\"");

            migrationBuilder.CreateIndex(
                name: "ix_follow_edges_followee_id",
                schema: "recommendations",
                table: "follow_edges",
                column: "followee_id");

            migrationBuilder.CreateIndex(
                name: "hidden_edges_by_target",
                schema: "recommendations",
                table: "hidden_edges",
                columns: new[] { "target_id", "owner_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "follow_edges",
                schema: "recommendations");

            migrationBuilder.DropTable(
                name: "hidden_edges",
                schema: "recommendations");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "recommendations");
        }
    }
}
