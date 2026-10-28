namespace WebApi.Analytics;

public record AnalyticsLoadResponse(IReadOnlyList<AnalyticsLoadItem> Items);

public record AnalyticsLoadItem(
    DateOnly Date,
    AnalyticsRoom Room,
    int ClassesCount,
    int TotalCapacity,
    int BookedSeats);

public record AnalyticsRoom(long Id, string Name);
