namespace WebApi.Enrollments;

public record JoinWaitlistResponse(long Id, long ClassId, int Seats, int Position, DateTime CreatedAt);

public record CancelWaitlistResponse(long Id, string Status);
