using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class Class : IEntity
{
    public long Id { get; set; }

    public long RoomId { get; set; }

    public string Title { get; set; } = null!;

    public DateTime StartsAt { get; set; }

    public int DurationMinutes { get; set; }

    public int Capacity { get; set; }

    public bool IsCancelled { get; set; }

    public Room Room { get; set; } = null!;

    public ICollection<Enrollment> Enrollments { get; set; } = new List<Enrollment>();
}
