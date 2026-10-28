using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public sealed class PassRepository(DbSet<Pass> dbSet) : GenericRepository<Pass>(dbSet), IPassRepository
{
    public Task<Pass> GetForUpdateAsync(long userId, CancellationToken ct = default)
    {
        return _dbSet.FromSqlInterpolated($"SELECT * FROM passes WHERE user_id = {userId} FOR UPDATE")
            .SingleAsync(ct);
    }

    public async Task<Pass?> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.UserId == userId, ct);
    }
}
