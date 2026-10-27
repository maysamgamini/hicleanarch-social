using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Posts.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "author_activity",
                schema: "posts",
                columns: table => new
                {
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_author_activity", x => x.author_id);
                });

            // Authors who posted before this table existed: their last post time, once.
            migrationBuilder.Sql("""
                INSERT INTO posts.author_activity (author_id, last_posted_at)
                SELECT author_id, max(created_at) FROM posts.posts GROUP BY author_id
                ON CONFLICT (author_id) DO NOTHING
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "author_activity",
                schema: "posts");
        }
    }
}
