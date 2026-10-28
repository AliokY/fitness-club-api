using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using WebApi.Enrollments;

namespace WebApi.Schedule;

public sealed class ScheduleService(ClubDbContext dbContext)
{
    private const int DefaultPageSize = 20;
    private const int MaximumPageSize = 100;
    private const int MaximumTokenLength = 4096;
    private static readonly Regex IsoTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ScheduleResponse> GetAsync(long userId, string? from, string? to,
        long? roomId, int? limit, string? continuationToken, CancellationToken ct = default)
    {
        if (from is null || to is null)
            throw new EnrollmentException(400, "Both from and to are required");
        if (!TryParseUtc(from, out var fromValue) || !TryParseUtc(to, out var toValue))
            throw new EnrollmentException(400, "from and to must be ISO 8601 date-time values");

        var fromUtc = fromValue.UtcDateTime;
        var toUtc = toValue.UtcDateTime;
        if (toUtc <= fromUtc)
            throw new EnrollmentException(400, "to must be later than from");

        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaximumPageSize)
            throw new EnrollmentException(400, "limit must be between 1 and 100");

        var cursor = continuationToken is null
            ? null
            : DecodeToken(continuationToken, fromUtc, toUtc, roomId);

        var query = dbContext.Classes.AsNoTracking()
            .Where(x => x.StartsAt >= fromUtc && x.StartsAt < toUtc);
        if (roomId.HasValue)
            query = query.Where(x => x.RoomId == roomId.Value);
        if (cursor is not null)
            query = query.Where(x => x.StartsAt > cursor.StartsAt
                || (x.StartsAt == cursor.StartsAt && x.Id > cursor.ClassId));

        // A single SQL query returns the page and both aggregates, so page fields
        // reflect one PostgreSQL snapshot even when enrollments change concurrently
        var rows = await query
            .OrderBy(x => x.StartsAt).ThenBy(x => x.Id)
            .Select(x => new ScheduleRow(
                x.Id, x.Title, x.StartsAt, x.DurationMinutes,
                x.Room.Id, x.Room.Name, x.Capacity,
                x.Enrollments.Where(e => e.Status == "active")
                    .Sum(e => (long?)e.Seats) ?? 0L,
                x.Enrollments.Where(e => e.Status == "active" && e.UserId == userId)
                    .Sum(e => (long?)e.Seats) ?? 0L))
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        var items = rows.Select(x => new ScheduleItemResponse(
            x.ClassId, x.Title, x.StartsAt, x.DurationMinutes,
            new ScheduleRoomResponse(x.RoomId, x.RoomName), x.Capacity,
            checked((int)Math.Max(0L, x.Capacity - x.BookedSeats)), checked((int)x.MySeats)))
            .ToArray();

        var next = hasMore && rows.Count > 0
            ? EncodeToken(new ScheduleCursor(fromUtc.Ticks, toUtc.Ticks, roomId,
                rows[^1].StartsAt.Ticks, rows[^1].ClassId))
            : null;
        return new ScheduleResponse(items, next);
    }

    private static bool TryParseUtc(string? value, out DateTimeOffset result)
    {
        if (value is not null && IsoTimestamp.IsMatch(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
            return true;
        result = default;
        return false;
    }

    private static string EncodeToken(ScheduleCursor cursor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor, JsonOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static ScheduleCursor DecodeToken(string token, DateTime fromUtc, DateTime toUtc, long? roomId)
    {
        try
        {
            if (token.Length is 0 or > MaximumTokenLength)
                throw new FormatException();
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var bytes = Convert.FromBase64String(base64);
            var cursor = JsonSerializer.Deserialize<ScheduleCursor>(bytes, JsonOptions);
            if (cursor is null || cursor.FromTicks != fromUtc.Ticks || cursor.ToTicks != toUtc.Ticks
                || cursor.RoomId != roomId || cursor.ClassId <= 0
                || cursor.StartsAtTicks < fromUtc.Ticks || cursor.StartsAtTicks >= toUtc.Ticks)
                throw new FormatException();
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new EnrollmentException(400, "Invalid continuationToken");
        }
    }

    private sealed record ScheduleCursor(long FromTicks, long ToTicks, long? RoomId,
        long StartsAtTicks, long ClassId)
    {
        public DateTime StartsAt => new(StartsAtTicks, DateTimeKind.Utc);
    }

    private sealed record ScheduleRow(long ClassId, string Title, DateTime StartsAt, int DurationMinutes,
        long RoomId, string RoomName, int Capacity, long BookedSeats, long MySeats);
}
