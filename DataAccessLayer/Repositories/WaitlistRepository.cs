using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class WaitlistRepository(ClubDbContext dbContext)
{
    public async Task<List<WaitlistEntry>> GetWaitingAsync(long classId, CancellationToken ct)
    {
        return await dbContext.WaitlistEntries
            .Where(x => x.ClassId == classId && x.Status == "waiting")
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(ct);
    }

    public Task<WaitlistEntry?> GetForUpdateAsync(long id, long userId, CancellationToken ct)
    {
        return dbContext.WaitlistEntries
            .FromSqlInterpolated($"SELECT * FROM waitlist_entries WHERE id = {id} AND user_id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
    }

    public Task<int> GetPositionAsync(long classId, DateTime createdAt, long id, CancellationToken ct)
    {
        return dbContext.WaitlistEntries.CountAsync(x => x.ClassId == classId && x.Status == "waiting"
            && (x.CreatedAt < createdAt || (x.CreatedAt == createdAt && x.Id <= id)), ct);
    }
}
