using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IPassRepository : IGenericRepository<Pass>
{
    Task<Pass> GetForUpdateAsync(long userId, CancellationToken ct = default);

    Task<Pass?> GetByUserAsync(long userId, CancellationToken ct = default);
}
