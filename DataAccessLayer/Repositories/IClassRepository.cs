using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<Class?> GetForUpdateAsync(long id, CancellationToken ct = default);

    Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default);
}
