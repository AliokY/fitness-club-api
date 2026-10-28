using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class Pass : IEntity
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public int RemainingVisits { get; set; }

    public DateOnly ValidUntil { get; set; }
}
