namespace WebApi.Enrollments;

public class EnrollmentException(int statusCode, string title, string? reason = null) : Exception(title)
{
    public int StatusCode { get; } = statusCode;
    public string? Reason { get; } = reason;
}
