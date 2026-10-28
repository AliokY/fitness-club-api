using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IEnrollmentRepository : IGenericRepository<Enrollment>
{
    Task<long> GetBookedSeatsAsync(long classId, CancellationToken ct = default);
    Task<long?> GetClassIdForUserAsync(long id, long userId, CancellationToken ct = default);
    Task<Enrollment?> GetForUpdateAsync(long id, long userId, CancellationToken ct = default);

    Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default);
}
