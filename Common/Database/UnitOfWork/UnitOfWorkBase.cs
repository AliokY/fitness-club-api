using Common.Database.Interfaces;
using Common.Database.Repositories.Generic;
using Microsoft.EntityFrameworkCore;

namespace Common.Database.UnitOfWork;

public interface IUnitOfWorkBase
{
    DbContext DbContext { get; }

    IGenericRepository<T> GetRepository<T>() where T : class, IEntity;

    Task SaveChangesAsync(CancellationToken ct = default);
}

public abstract class UnitOfWorkBase : IUnitOfWorkBase
{
    private readonly Dictionary<Type, object> _repositories;

    protected UnitOfWorkBase(DbContext dbContext)
    {
        DbContext = dbContext;
        _repositories = new Dictionary<Type, object>();
    }

    public DbContext DbContext { get; }

    public IGenericRepository<T> GetRepository<T>() where T : class, IEntity
    {
        var type = typeof(T);

        if (!_repositories.TryGetValue(type, out var repository))
        {
            repository = new GenericRepository<T>(DbContext.Set<T>());
            _repositories[type] = repository;
        }

        return (IGenericRepository<T>)repository;
    }

    protected TRepository GetRepository<TEntity, TRepository>(Func<DbSet<TEntity>, TRepository> factory)
        where TEntity : class, IEntity
        where TRepository : class
    {
        var type = typeof(TRepository);

        if (!_repositories.TryGetValue(type, out var repository))
        {
            repository = factory(DbContext.Set<TEntity>());
            _repositories[type] = repository;
        }

        return (TRepository)repository;
    }

    public virtual async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await DbContext.SaveChangesAsync(ct);
    }
}
