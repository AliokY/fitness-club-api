using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class PassRepository : GenericRepository<Pass>, IPassRepository
{
    public Task<Pass> GetForUpdateAsync(long userId, CancellationToken ct = default)
    {
        return _dbSet.FromSqlInterpolated($"SELECT * FROM passes WHERE user_id = {userId} FOR UPDATE")
            .SingleAsync(ct);
    }

    public PassRepository(DbSet<Pass> dbSet)
        : base(dbSet)
    {
    }

    public async Task<Pass?> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.UserId == userId, ct);
    }
}
