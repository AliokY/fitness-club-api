using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class WaitlistEntry : IEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long ClassId { get; set; }
    public int Seats { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long? EnrollmentId { get; set; }
    public string? CompletionReason { get; set; }
}
