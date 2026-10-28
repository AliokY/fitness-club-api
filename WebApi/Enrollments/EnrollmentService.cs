using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Enrollments;

public class EnrollmentService(IUnitOfWork unitOfWork, IdempotencyRepository idempotency, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WaitlistRepository _waitlist = new((ClubDbContext)unitOfWork.DbContext);

    public async Task<CreateEnrollmentResponse> CreateAsync(
        long userId, long classId, int seats, string key, CancellationToken ct = default)
    {
        if (seats < 1 || string.IsNullOrWhiteSpace(key))
            throw new EnrollmentException(400, "Invalid enrollment request");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Scope the key to the user and operation, index its hash, and compare the original value
        var record = await idempotency.AcquireAsync(userId, "create-enrollment", hash, key, classId, seats, ct);
        if (record.Key != key || record.ClassId != classId || record.Seats != seats)
            throw new EnrollmentException(409, "Idempotency key has different request data", "idempotency-key-reused");

        if (record.ResponseJson is not null)
        {
            var previous = JsonSerializer.Deserialize<CreateEnrollmentResponse>(record.ResponseJson, JsonOptions)!;
            await transaction.CommitAsync(ct);
            return previous;
        }

        var scheduledClass = await unitOfWork.Class.GetForUpdateAsync(classId, ct)
            ?? throw new EnrollmentException(404, "Class not found");
        var pass = await unitOfWork.Pass.GetForUpdateAsync(userId, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        // When multiple rules fail, use this priority: cancellation, start time, expiration, balance, capacity
        if (scheduledClass.IsCancelled) Reject("class-cancelled");
        if (scheduledClass.StartsAt <= now) Reject("class-started");
        if (pass.ValidUntil < DateOnly.FromDateTime(now)) Reject("pass-expired");
        if (pass.RemainingVisits < seats) Reject("pass-exhausted");

        var bookedSeats = await unitOfWork.Enrollment.GetBookedSeatsAsync(classId, ct);
        if (bookedSeats + seats > scheduledClass.Capacity) Reject("capacity-exceeded");

        // PostgreSQL stores microseconds, so use the same precision for the initial and repeated responses
        now = TruncateToMicroseconds(now);
        var enrollment = new Enrollment
        {
            UserId = userId, ClassId = classId, Seats = seats, Status = "active", CreatedAt = now
        };
        await unitOfWork.Enrollment.CreateAsync(enrollment, ct);
        pass.RemainingVisits -= seats;
        await unitOfWork.SaveChangesAsync(ct);

        var response = new CreateEnrollmentResponse(enrollment.Id, classId, seats, "active", now);
        record.ResponseJson = JsonSerializer.Serialize(response, JsonOptions);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    public async Task<EnrollmentCardResponse> GetCardAsync(long userId, long id, CancellationToken ct = default)
    {
        // Read the card in one SQL query without caching, including cancelled classes and the current pass
        return await (
            from enrollment in unitOfWork.Enrollment.Query().IgnoreQueryFilters().AsNoTracking()
            join pass in unitOfWork.Pass.Query().AsNoTracking() on enrollment.UserId equals pass.UserId
            where enrollment.Id == id && enrollment.UserId == userId
            select new EnrollmentCardResponse(
                enrollment.Id, enrollment.Status, enrollment.Seats, enrollment.CreatedAt,
                new EnrollmentClassResponse(enrollment.Class.Id, enrollment.Class.Title, enrollment.Class.StartsAt,
                    enrollment.Class.DurationMinutes, enrollment.Class.IsCancelled,
                    new EnrollmentRoomResponse(enrollment.Class.Room.Id, enrollment.Class.Room.Name)),
                new EnrollmentPassResponse(pass.RemainingVisits, pass.ValidUntil)))
            .SingleOrDefaultAsync(ct) ?? throw new EnrollmentException(404, "Enrollment not found");
    }

    public async Task<EnrollmentCardResponse> ChangeSeatsAsync(long userId, long id, int seats, CancellationToken ct = default)
    {
        if (seats < 1) throw new EnrollmentException(400, "Seats must be positive");
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (enrollment, scheduledClass, passes) = await LockEnrollmentAsync(userId, id, ct);
        var pass = passes[userId];
        var now = clock.GetUtcNow().UtcDateTime;

        // Check enrollment status first; an unchanged seat count does not bypass class validation
        if (enrollment.Status == "cancelled") Reject("enrollment-cancelled");
        if (scheduledClass.IsCancelled) Reject("class-cancelled");
        if (scheduledClass.StartsAt <= now) Reject("class-started");

        var difference = seats - enrollment.Seats;
        if (difference > 0)
        {
            if (pass.ValidUntil < DateOnly.FromDateTime(now)) Reject("pass-expired");
            if (pass.RemainingVisits < difference) Reject("pass-exhausted");
            var bookedSeats = await unitOfWork.Enrollment.GetBookedSeatsAsync(scheduledClass.Id, ct);
            if (bookedSeats + difference > scheduledClass.Capacity) Reject("capacity-exceeded");
        }

        enrollment.Seats = seats;
        pass.RemainingVisits = checked(pass.RemainingVisits - difference);
        await unitOfWork.SaveChangesAsync(ct);
        if (difference < 0)
            await PromoteWaitlistAsync(scheduledClass, passes, ct);
        var response = await GetCardAsync(userId, id, ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    public async Task<CancelEnrollmentResponse> CancelAsync(long userId, long id, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (enrollment, scheduledClass, passes) = await LockEnrollmentAsync(userId, id, ct);
        var pass = passes[userId];

        if (enrollment.Status != "cancelled")
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (!scheduledClass.IsCancelled && scheduledClass.StartsAt <= now) Reject("class-started");
            var refund = scheduledClass.IsCancelled || scheduledClass.StartsAt - now >= TimeSpan.FromHours(2)
                ? enrollment.Seats : 0;
            enrollment.Status = "cancelled";
            enrollment.CancelledAt = TruncateToMicroseconds(now);
            enrollment.RefundedVisits = refund;
            pass.RemainingVisits = checked(pass.RemainingVisits + refund);
            await unitOfWork.SaveChangesAsync(ct);
            await PromoteWaitlistAsync(scheduledClass, passes, ct);
        }

        var response = new CancelEnrollmentResponse(enrollment.Id, "cancelled", enrollment.RefundedVisits);
        await transaction.CommitAsync(ct);
        return response;
    }

    private async Task<(Enrollment Enrollment, Class Class, Dictionary<long, Pass> Passes)> LockEnrollmentAsync(
        long userId, long id, CancellationToken ct)
    {
        var classId = await unitOfWork.Enrollment.GetClassIdForUserAsync(id, userId, ct)
            ?? throw new EnrollmentException(404, "Enrollment not found");
        var scheduledClass = await unitOfWork.Class.GetForUpdateAsync(classId, ct)
            ?? throw new EnrollmentException(404, "Enrollment not found");
        var waiting = await _waitlist.GetWaitingAsync(classId, ct);
        var userIds = waiting.Select(x => x.UserId).Append(userId).Distinct().Order().ToArray();
        var passes = new Dictionary<long, Pass>(userIds.Length);
        foreach (var ownerId in userIds)
            passes.Add(ownerId, await unitOfWork.Pass.GetForUpdateAsync(ownerId, ct));
        var enrollment = await unitOfWork.Enrollment.GetForUpdateAsync(id, userId, ct)
            ?? throw new EnrollmentException(404, "Enrollment not found");
        return (enrollment, scheduledClass, passes);
    }

    public async Task<JoinWaitlistResponse> JoinWaitlistAsync(
        long userId, long classId, int seats, string key, CancellationToken ct = default)
    {
        if (seats < 1 || string.IsNullOrWhiteSpace(key))
            throw new EnrollmentException(400, "Invalid waitlist request");

        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var record = await idempotency.AcquireAsync(userId, "join-waitlist", keyHash, key, classId, seats, ct);
        if (record.Key != key || record.ClassId != classId || record.Seats != seats)
            Reject("idempotency-key-reused");
        if (record.ResponseJson is not null)
        {
            var previous = JsonSerializer.Deserialize<JoinWaitlistResponse>(record.ResponseJson, JsonOptions)!;
            await transaction.CommitAsync(ct);
            return previous;
        }

        var scheduledClass = await unitOfWork.Class.GetForUpdateAsync(classId, ct)
            ?? throw new EnrollmentException(404, "Class not found");
        var now = clock.GetUtcNow().UtcDateTime;
        if (scheduledClass.IsCancelled) Reject("class-cancelled");
        if (scheduledClass.StartsAt <= now) Reject("class-started");
        if (await unitOfWork.DbContext.Set<WaitlistEntry>()
                .AnyAsync(x => x.UserId == userId && x.ClassId == classId && x.Status == "waiting", ct))
            Reject("already-waiting");

        var bookedSeats = await unitOfWork.Enrollment.GetBookedSeatsAsync(classId, ct);
        if (scheduledClass.Capacity - bookedSeats >= seats) Reject("seats-available");

        now = TruncateToMicroseconds(now);
        var entry = new WaitlistEntry
        {
            UserId = userId, ClassId = classId, Seats = seats, Status = "waiting", CreatedAt = now
        };
        unitOfWork.DbContext.Set<WaitlistEntry>().Add(entry);
        await unitOfWork.SaveChangesAsync(ct);
        var position = await _waitlist.GetPositionAsync(classId, now, entry.Id, ct);
        var response = new JoinWaitlistResponse(entry.Id, classId, seats, position, now);
        record.ResponseJson = JsonSerializer.Serialize(response, JsonOptions);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    public async Task<CancelWaitlistResponse> CancelWaitlistAsync(long userId, long id, CancellationToken ct = default)
    {
        var classId = await unitOfWork.DbContext.Set<WaitlistEntry>()
            .Where(x => x.Id == id && x.UserId == userId)
            .Select(x => (long?)x.ClassId).SingleOrDefaultAsync(ct)
            ?? throw new EnrollmentException(404, "Waitlist entry not found");

        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (await unitOfWork.Class.GetForUpdateAsync(classId, ct) is null)
            throw new EnrollmentException(404, "Waitlist entry not found");
        var entry = await _waitlist.GetForUpdateAsync(id, userId, ct)
            ?? throw new EnrollmentException(404, "Waitlist entry not found");
        if (entry.Status == "fulfilled") Reject("waitlist-fulfilled");

        if (entry.Status == "waiting")
        {
            entry.Status = "cancelled";
            entry.CompletionReason = "cancelled-by-user";
            entry.CompletedAt = TruncateToMicroseconds(clock.GetUtcNow().UtcDateTime);
            await unitOfWork.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new CancelWaitlistResponse(entry.Id, "cancelled");
    }

    private async Task PromoteWaitlistAsync(Class scheduledClass, IReadOnlyDictionary<long, Pass> passes,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (scheduledClass.IsCancelled || scheduledClass.StartsAt <= now)
            return;

        var waiting = await _waitlist.GetWaitingAsync(scheduledClass.Id, ct);
        var bookedSeats = await unitOfWork.Enrollment.GetBookedSeatsAsync(scheduledClass.Id, ct);
        var freeSeats = Math.Max(0L, scheduledClass.Capacity - bookedSeats);
        now = TruncateToMicroseconds(now);

        foreach (var entry in waiting)
        {
            if (entry.Seats > scheduledClass.Capacity)
            {
                entry.Status = "cancelled";
                entry.CompletionReason = "seats-exceed-capacity";
                entry.CompletedAt = now;
                continue;
            }
            var pass = passes[entry.UserId];
            var invalidReason = pass.ValidUntil < DateOnly.FromDateTime(now)
                ? "pass-expired"
                : pass.RemainingVisits < entry.Seats ? "pass-exhausted" : null;
            if (invalidReason is not null)
            {
                // Remove the entry from the waitlist if the pass is invalid or has insufficient visits
                entry.Status = "cancelled";
                entry.CompletionReason = invalidReason;
                entry.CompletedAt = now;
                continue;
            }
            if (entry.Seats > freeSeats)
                break;

            var enrollment = new Enrollment
            {
                UserId = entry.UserId, ClassId = scheduledClass.Id, Seats = entry.Seats,
                Status = "active", CreatedAt = now
            };
            await unitOfWork.Enrollment.CreateAsync(enrollment, ct);
            // Save first to obtain the database-generated ID, then set the foreign key on the waitlist entry
            // Both saves remain inside the enrollment cancellation or seat-change transaction
            await unitOfWork.SaveChangesAsync(ct);
            pass.RemainingVisits -= entry.Seats;
            entry.Status = "fulfilled";
            entry.CompletionReason = null;
            entry.CompletedAt = now;
            entry.EnrollmentId = enrollment.Id;
            freeSeats -= entry.Seats;
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private static void Reject(string reason) => throw new EnrollmentException(409, "Enrollment operation cannot be completed", reason);
}
