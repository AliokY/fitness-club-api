namespace DataAccessLayer.Entities;

public class IdempotencyRecord
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Operation { get; set; } = null!;
    public string KeyHash { get; set; } = null!;
    public string Key { get; set; } = null!;
    public long ClassId { get; set; }
    public int Seats { get; set; }
    public string? ResponseJson { get; set; }
}
