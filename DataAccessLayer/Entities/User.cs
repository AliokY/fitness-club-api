using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class User : IEntity
{
    public long Id { get; set; }

    public string Email { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public string Role { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
