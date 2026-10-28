using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class EnrollmentRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "refunded_visits",
                table: "enrollments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Refund history is unavailable, so infer the result from the current snapshot without changing balances
            migrationBuilder.Sql("""
                UPDATE enrollments AS e
                SET refunded_visits = CASE
                    WHEN c.is_cancelled OR e.cancelled_at <= c.starts_at - INTERVAL '2 hours' THEN e.seats
                    ELSE 0
                END
                FROM classes AS c
                WHERE c.id = e.class_id AND e.status = 'cancelled';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refunded_visits",
                table: "enrollments");
        }
    }
}
