using System.ComponentModel.DataAnnotations;

namespace WebApi.Enrollments;

public class CreateEnrollmentRequest
{
    [Required]
    public long? ClassId { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; init; }
}
