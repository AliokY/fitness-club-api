using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class IdempotencyRepository(ClubDbContext dbContext)
{
    public async Task<IdempotencyRecord> AcquireAsync(
        long userId, string operation, string hash, string key, long classId, int seats, CancellationToken ct)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO idempotency_records (user_id, operation, key_hash, key, class_id, seats)
            VALUES ({userId}, {operation}, {hash}, {key}, {classId}, {seats})
            ON CONFLICT (user_id, operation, key_hash) DO NOTHING
            """, ct);

        return await dbContext.Set<IdempotencyRecord>()
            .FromSqlInterpolated($"""
                SELECT * FROM idempotency_records
                WHERE user_id = {userId} AND operation = {operation} AND key_hash = {hash}
                FOR UPDATE
                """)
            .SingleAsync(ct);
    }
}
