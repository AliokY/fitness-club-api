namespace WebApi.Enrollments;

public record CreateEnrollmentResponse(long Id, long ClassId, int Seats, string Status, DateTime CreatedAt);
