using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.Migrations
{
    /// <inheritdoc />
    public partial class HourlyNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "actors",
                schema: "notifications",
                table: "notifications",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<int>(
                name: "count",
                schema: "notifications",
                table: "notifications",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "summarized_at",
                schema: "notifications",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "summary_due",
                schema: "notifications",
                table: "notifications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "notifications",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "window_start",
                schema: "notifications",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "follower_counts",
                schema: "notifications",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    followers = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_follower_counts", x => x.account_id);
                });

            // The rows written one per follow before this: each takes its hour, and an hour's rows of one
            // recipient become one, counted, naming its first three actors.
            migrationBuilder.Sql("""
                UPDATE notifications.notifications
                SET window_start = date_trunc('hour', created_at), actors = ARRAY[actor_id], updated_at = created_at;
                WITH grouped AS (
                    SELECT recipient_id, kind, window_start, min(id) AS keep, count(*) AS n,
                           (array_agg(actor_id ORDER BY id))[1:3] AS first_three, max(created_at) AS last_at
                    FROM notifications.notifications GROUP BY recipient_id, kind, window_start HAVING count(*) > 1)
                UPDATE notifications.notifications n SET count = g.n, actors = g.first_three, updated_at = g.last_at
                FROM grouped g WHERE n.id = g.keep;
                DELETE FROM notifications.notifications n
                USING (SELECT recipient_id, kind, window_start, min(id) AS keep FROM notifications.notifications
                       GROUP BY recipient_id, kind, window_start HAVING count(*) > 1) g
                WHERE n.recipient_id = g.recipient_id AND n.kind = g.kind AND n.window_start = g.window_start AND n.id <> g.keep;
                """);

            migrationBuilder.CreateIndex(
                name: "notifications_one_per_hour",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "recipient_id", "kind", "window_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "notifications_summaries_due",
                schema: "notifications",
                table: "notifications",
                column: "window_start",
                filter: "summary_due AND summarized_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "follower_counts",
                schema: "notifications");

            migrationBuilder.DropIndex(
                name: "notifications_one_per_hour",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "notifications_summaries_due",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "actors",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "count",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "summarized_at",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "summary_due",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "window_start",
                schema: "notifications",
                table: "notifications");
        }
    }
}
