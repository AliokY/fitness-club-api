using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class Room : IEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = null!;
}
