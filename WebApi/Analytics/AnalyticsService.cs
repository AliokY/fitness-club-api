using System.Globalization;
using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using WebApi.Enrollments;

namespace WebApi.Analytics;

public sealed class AnalyticsService(ClubDbContext dbContext)
{
    public async Task<AnalyticsLoadResponse> GetLoadAsync(string? from, string? to, long? roomId,
        CancellationToken ct = default)
    {
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fromDate)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var toDate))
            throw new EnrollmentException(400, "from and to are required ISO dates in yyyy-MM-dd format");
        if (toDate < fromDate)
            throw new EnrollmentException(400, "to must be on or after from");

        var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DateTime? toExclusiveUtc = toDate == DateOnly.MaxValue
            ? null
            : toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // LATERAL counts booked seats only for classes in the selected period
        // Grouping and all three aggregates run in a single PostgreSQL statement
        var rows = await dbContext.Database.SqlQuery<AnalyticsAggregateRow>($"""
            SELECT
                (c.starts_at AT TIME ZONE 'UTC')::date AS "Date",
                r.id AS "RoomId",
                r.name AS "RoomName",
                COUNT(*) AS "ClassesCount",
                COALESCE(SUM(c.capacity), 0)::bigint AS "TotalCapacity",
                COALESCE(SUM(booked.booked_seats), 0)::bigint AS "BookedSeats"
            FROM classes AS c
            INNER JOIN rooms AS r ON r.id = c.room_id
            LEFT JOIN LATERAL (
                SELECT COALESCE(SUM(e.seats), 0)::bigint AS booked_seats
                FROM enrollments AS e
                WHERE e.class_id = c.id AND e.status = 'active'
            ) AS booked ON TRUE
            WHERE c.is_cancelled = FALSE
                AND c.starts_at >= {fromUtc}
                AND c.starts_at < COALESCE({toExclusiveUtc}, 'infinity'::timestamp with time zone)
                AND ({roomId}::bigint IS NULL OR c.room_id = {roomId}::bigint)
            GROUP BY (c.starts_at AT TIME ZONE 'UTC')::date, r.id, r.name
            ORDER BY "Date", r.id
            """).ToListAsync(ct);

        var items = rows.Select(row => new AnalyticsLoadItem(
            row.Date,
            new AnalyticsRoom(row.RoomId, row.RoomName),
            checked((int)row.ClassesCount),
            checked((int)row.TotalCapacity),
            checked((int)row.BookedSeats)))
            .ToArray();
        return new AnalyticsLoadResponse(items);
    }

    private sealed class AnalyticsAggregateRow
    {
        public DateOnly Date { get; init; }
        public long RoomId { get; init; }
        public string RoomName { get; init; } = null!;
        public long ClassesCount { get; init; }
        public long TotalCapacity { get; init; }
        public long BookedSeats { get; init; }
    }
}
