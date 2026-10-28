using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class Enrollment : IEntity
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public int RefundedVisits { get; set; }

    public Class Class { get; set; } = null!;
}
