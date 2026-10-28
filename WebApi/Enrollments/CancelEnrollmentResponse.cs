namespace WebApi.Enrollments;

public record CancelEnrollmentResponse(long Id, string Status, int RefundedVisits);
