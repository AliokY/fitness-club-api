namespace WebApi.Enrollments;

public record EnrollmentCardResponse(
    long Id, string Status, int Seats, DateTime CreatedAt, EnrollmentClassResponse Class, EnrollmentPassResponse Pass);

public record EnrollmentClassResponse(
    long Id, string Title, DateTime StartsAt, int DurationMinutes, bool IsCancelled, EnrollmentRoomResponse Room);

public record EnrollmentRoomResponse(long Id, string Name);

public record EnrollmentPassResponse(int RemainingVisits, DateOnly ValidUntil);
