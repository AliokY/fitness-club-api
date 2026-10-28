namespace WebApi.Schedule;

public record ScheduleResponse(IReadOnlyList<ScheduleItemResponse> Items, string? ContinuationToken);

public record ScheduleItemResponse(
    long ClassId,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    ScheduleRoomResponse Room,
    int Capacity,
    int FreeSeats,
    int MySeats);

public record ScheduleRoomResponse(long Id, string Name);
