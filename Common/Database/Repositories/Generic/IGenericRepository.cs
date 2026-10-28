using Common.Database.Interfaces;

namespace Common.Database.Repositories.Generic;

public interface IGenericRepository<TEntity> where TEntity : class, IEntity
{
    IQueryable<TEntity> Query();

    Task<TEntity?> GetByIdAsync(long id, CancellationToken ct = default);

    Task<List<TEntity>> GetAllAsync(
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? filter = null,
        CancellationToken ct = default);

    Task<bool> AnyAsync(
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? filter = null,
        CancellationToken ct = default);

    Task<int> CountAsync(
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? filter = null,
        CancellationToken ct = default);

    Task<TEntity> CreateAsync(TEntity entity, CancellationToken ct = default);

    TEntity Update(TEntity entity);

    TEntity Delete(TEntity entity);
}
