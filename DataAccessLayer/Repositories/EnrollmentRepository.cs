using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class EnrollmentRepository : GenericRepository<Enrollment>, IEnrollmentRepository
{
    public Task<long?> GetClassIdForUserAsync(long id, long userId, CancellationToken ct = default)
    {
        return _dbSet.Where(x => x.Id == id && x.UserId == userId)
            .Select(x => (long?)x.ClassId).SingleOrDefaultAsync(ct);
    }

    public Task<Enrollment?> GetForUpdateAsync(long id, long userId, CancellationToken ct = default)
    {
        return _dbSet.FromSqlInterpolated($"SELECT * FROM enrollments WHERE id = {id} AND user_id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
    }

    public async Task<long> GetBookedSeatsAsync(long classId, CancellationToken ct = default)
    {
        return await _dbSet.Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => (long)x.Seats, ct);
    }

    public EnrollmentRepository(DbSet<Enrollment> dbSet)
        : base(dbSet)
    {
    }

    public async Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
    }
}
