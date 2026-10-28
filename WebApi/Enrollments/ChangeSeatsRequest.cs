using System.ComponentModel.DataAnnotations;

namespace WebApi.Enrollments;

public class JoinWaitlistRequest
{
    [Required]
    public long? ClassId { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; init; }
}

public class ChangeSeatsRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; init; }
}
