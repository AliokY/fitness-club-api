using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Seeding;

public static class DatabaseSeeder
{
    public const string DefaultPassword = "Password123!";

    private const int UserCount = 200;
    private const int AdminCount = 5;
    private const int RoomCount = 10;
    private const int ClassCount = 200;
    private const int EnrollmentCount = 250;

    private static readonly string[] RoomNames =
    [
        "Hall A", "Hall B", "Hall C", "Hall D", "Hall E",
        "Hall F", "Hall G", "Hall H", "Hall J", "Hall K"
    ];

    private static readonly string[] ClassTitles =
    [
        "Yoga", "Pilates", "Stretching", "Functional Training", "Boxing",
        "Cycling", "Aerobics", "Barre", "TRX", "Dance Mix"
    ];

    private static readonly int[] Durations = [45, 60, 90];

    public static async Task SeedAsync(ClubDbContext dbContext, CancellationToken ct = default)
    {
        if (await dbContext.Users.AnyAsync(ct))
        {
            return;
        }

        var random = new Random(20260101);
        var now = TruncateToMinute(DateTime.UtcNow);
        var today = DateOnly.FromDateTime(now);
        var passwordHash = new PasswordHasher<User>().HashPassword(new User(), DefaultPassword);

        var rooms = new List<Room>();

        for (var i = 0; i < RoomCount; i++)
        {
            rooms.Add(new Room { Name = RoomNames[i] });
        }

        await dbContext.Rooms.AddRangeAsync(rooms, ct);

        var users = new List<User>();

        for (var i = 1; i <= UserCount; i++)
        {
            users.Add(new User
            {
                Email = $"user{i:D3}@club.test",
                PasswordHash = passwordHash,
                Role = "user",
                CreatedAt = now.AddDays(-random.Next(30, 400))
            });
        }

        for (var i = 1; i <= AdminCount; i++)
        {
            users.Add(new User
            {
                Email = $"admin{i:D3}@club.test",
                PasswordHash = passwordHash,
                Role = "admin",
                CreatedAt = now.AddDays(-random.Next(30, 400))
            });
        }

        await dbContext.Users.AddRangeAsync(users, ct);
        await dbContext.SaveChangesAsync(ct);

        var passes = new List<Pass>();

        for (var i = 0; i < users.Count; i++)
        {
            var position = i + 1;
            var remainingVisits = position % 20 == 0
                ? 0
                : position % 20 == 10
                    ? 1
                    : 10 + random.Next(0, 31);

            var validUntil = position % 17 == 0
                ? today.AddDays(-random.Next(1, 31))
                : today.AddDays(30 + random.Next(0, 151));

            passes.Add(new Pass
            {
                UserId = users[i].Id,
                RemainingVisits = remainingVisits,
                ValidUntil = validUntil
            });
        }

        await dbContext.Passes.AddRangeAsync(passes, ct);

        var classes = new List<Class>();

        for (var i = 0; i < ClassCount; i++)
        {
            var startsAt = now.AddMinutes((i - ClassCount / 2) * 432);

            if (i > 0 && i % 40 == 0)
            {
                startsAt = classes[i - 1].StartsAt;
            }

            classes.Add(new Class
            {
                RoomId = rooms[i % RoomCount].Id,
                Title = ClassTitles[i % ClassTitles.Length],
                StartsAt = startsAt,
                DurationMinutes = Durations[i % Durations.Length],
                Capacity = 8 + random.Next(0, 23),
                IsCancelled = i % 13 == 0
            });
        }

        var soonOffsets = new[] { 20, 50, 80, 100, 115 };

        for (var i = 0; i < soonOffsets.Length; i++)
        {
            classes[150 + i].StartsAt = now.AddMinutes(soonOffsets[i]);
            classes[150 + i].IsCancelled = false;
        }

        await dbContext.Classes.AddRangeAsync(classes, ct);
        await dbContext.SaveChangesAsync(ct);

        var takenSeats = new Dictionary<long, int>();
        var enrollments = new List<Enrollment>();
        var passesByUser = passes.ToDictionary(x => x.UserId);

        for (var i = 0; i < EnrollmentCount; i++)
        {
            var user = users[random.Next(0, UserCount)];
            var scheduledClass = classes[random.Next(0, ClassCount)];
            var seats = 1 + random.Next(0, 3);
            var cancelled = i % 5 == 0;
            var createdAt = CreatedBefore(scheduledClass.StartsAt, now, random);
            var pass = passesByUser[user.Id];

            if (!cancelled)
            {
                takenSeats.TryGetValue(scheduledClass.Id, out var taken);

                if (taken + seats > scheduledClass.Capacity || pass.RemainingVisits < seats)
                {
                    continue;
                }

                takenSeats[scheduledClass.Id] = taken + seats;
                pass.RemainingVisits -= seats;
            }

            var cancelledAt = cancelled ? CancelledBetween(createdAt, scheduledClass.StartsAt, random) : (DateTime?)null;

            enrollments.Add(new Enrollment
            {
                UserId = user.Id,
                ClassId = scheduledClass.Id,
                Seats = seats,
                Status = cancelled ? "cancelled" : "active",
                CreatedAt = createdAt,
                CancelledAt = cancelledAt,
                // Assign calculated refunds to existing cancellations without changing original balances
                RefundedVisits = cancelled && (scheduledClass.IsCancelled || cancelledAt <= scheduledClass.StartsAt.AddHours(-2))
                    ? seats : 0
            });
        }

        var freeSeatsTargets = new[] { 0, 0, 1, 1, 2, 2 };

        var filledClasses = classes
            .Where(x => !x.IsCancelled && x.StartsAt > now.AddHours(3))
            .OrderBy(x => x.Id)
            .Take(freeSeatsTargets.Length)
            .ToList();

        var donorPosition = 0;

        for (var i = 0; i < filledClasses.Count; i++)
        {
            var scheduledClass = filledClasses[i];

            takenSeats.TryGetValue(scheduledClass.Id, out var taken);

            while (scheduledClass.Capacity - taken > freeSeatsTargets[i])
            {
                var seats = Math.Min(2, scheduledClass.Capacity - taken - freeSeatsTargets[i]);
                var donor = FindDonor(passes, today, seats, ref donorPosition);

                if (donor == null)
                {
                    break;
                }

                donor.RemainingVisits -= seats;
                taken += seats;

                enrollments.Add(new Enrollment
                {
                    UserId = donor.UserId,
                    ClassId = scheduledClass.Id,
                    Seats = seats,
                    Status = "active",
                    CreatedAt = CreatedBefore(scheduledClass.StartsAt, now, random),
                    CancelledAt = null
                });
            }

            takenSeats[scheduledClass.Id] = taken;
        }

        var repeatedClasses = classes
            .Where(x => !x.IsCancelled && x.StartsAt > now.AddDays(1))
            .OrderBy(x => x.Id)
            .Skip(20)
            .Take(3)
            .ToList();

        foreach (var scheduledClass in repeatedClasses)
        {
            var donor = FindDonor(passes, today, 2, ref donorPosition);

            takenSeats.TryGetValue(scheduledClass.Id, out var taken);

            if (donor == null || taken + 2 > scheduledClass.Capacity)
            {
                continue;
            }

            donor.RemainingVisits -= 2;
            takenSeats[scheduledClass.Id] = taken + 2;

            for (var i = 0; i < 2; i++)
            {
                enrollments.Add(new Enrollment
                {
                    UserId = donor.UserId,
                    ClassId = scheduledClass.Id,
                    Seats = 1,
                    Status = "active",
                    CreatedAt = CreatedBefore(scheduledClass.StartsAt, now, random),
                    CancelledAt = null
                });
            }
        }

        await dbContext.Enrollments.AddRangeAsync(enrollments, ct);
        await dbContext.SaveChangesAsync(ct);
    }

    private static DateTime CreatedBefore(DateTime startsAt, DateTime now, Random random)
    {
        var upperBound = startsAt < now ? startsAt : now;

        return upperBound.AddHours(-random.Next(1, 721));
    }

    private static DateTime CancelledBetween(DateTime createdAt, DateTime startsAt, Random random)
    {
        var window = startsAt - createdAt;
        var offset = TimeSpan.FromHours(random.Next(1, 48));

        return createdAt + (offset < window ? offset : window * 0.5);
    }

    private static Pass? FindDonor(List<Pass> passes, DateOnly today, int seats, ref int position)
    {
        for (var attempt = 0; attempt < passes.Count; attempt++)
        {
            var pass = passes[position % passes.Count];

            position++;

            if (pass.RemainingVisits >= seats && pass.ValidUntil >= today)
            {
                return pass;
            }
        }

        return null;
    }

    private static DateTime TruncateToMinute(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Utc);
    }
}
