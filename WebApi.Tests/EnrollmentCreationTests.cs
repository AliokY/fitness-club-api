using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.Seeding;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.Enrollments;
using WebApi.RateLimiting;

namespace WebApi.Tests;

[TestClass]
[DoNotParallelize]
public partial class EnrollmentCreationTests
{
    private static ApiApplicationFactory _factory = null!;
    private static readonly DateTime Now = new(2030, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _factory = new ApiApplicationFactory();
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = null!;

    [TestInitialize]
    public void Start()
    {
        if (_host is not null) return;
        _host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedClock());
        }));
        using var client = _host.CreateClient();
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(new Npgsql.NpgsqlConnectionStringBuilder(TestDatabase.PostgresConnectionString).Database,
            db.Database.GetDbConnection().Database, "Tests must use the isolated database");
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        _host?.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task CreatesExactResponseAndChargesVisits()
    {
        var (user, scheduledClass) = await ScenarioAsync();
        using var client = Client(user);
        using var response = await CreateAsync(client, scheduledClass.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(new[] { "id", "classId", "seats", "status", "createdAt" },
            json.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(2, json.RootElement.GetProperty("seats").GetInt32());
        Assert.AreEqual(Now, json.RootElement.GetProperty("createdAt").GetDateTime());
        await AssertStateAsync(user.Id, scheduledClass.Id, 8, 1, 2);
    }

    [TestMethod]
    public async Task ReplaysOriginalResponseAfterEnrollmentAndPassChange()
    {
        var (user, scheduledClass) = await ScenarioAsync();
        using var client = Client(user);
        var key = Guid.NewGuid().ToString();
        using var first = await CreateAsync(client, scheduledClass.Id, 2, key);
        var original = await first.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, original);
        await WithDbAsync(async db =>
        {
            var enrollment = await db.Enrollments.SingleAsync(x => x.UserId == user.Id);
            enrollment.Status = "cancelled";
            enrollment.Seats = 5;
            var pass = await db.Passes.SingleAsync(x => x.UserId == user.Id);
            pass.RemainingVisits = 0;
            pass.ValidUntil = DateOnly.FromDateTime(Now.AddDays(-1));
            var entity = await db.Classes.IgnoreQueryFilters().SingleAsync(x => x.Id == scheduledClass.Id);
            entity.IsCancelled = true;
            await db.SaveChangesAsync();
        });
        using var replay = await CreateAsync(client, scheduledClass.Id, 2, key);
        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
        Assert.AreEqual(original, await replay.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task DifferentPayloadConflictsButOtherUserHasIndependentKey()
    {
        var (user, scheduledClass) = await ScenarioAsync();
        var other = await AddUserAsync();
        using var client = Client(user);
        using var otherClient = Client(other);
        var key = Guid.NewGuid().ToString();
        using var first = await CreateAsync(client, scheduledClass.Id, 1, key);
        using var conflict = await CreateAsync(client, scheduledClass.Id, 2, key);
        await AssertReasonAsync(conflict, "idempotency-key-reused");
        using var independent = await CreateAsync(otherClient, scheduledClass.Id, 1, key);
        Assert.AreEqual(HttpStatusCode.Created, independent.StatusCode);
        await AssertStateAsync(user.Id, scheduledClass.Id, 9, 1, 1);
    }

    [TestMethod]
    public async Task SameUserCanCreateSeveralBookingsAndUnknownFieldsAreIgnored()
    {
        var (user, scheduledClass) = await ScenarioAsync();
        using var client = Client(user);
        using var first = await CreateAsync(client, scheduledClass.Id, 1);
        using var request = Request($"{{\"classId\":{scheduledClass.Id},\"seats\":2,\"userId\":999,\"extra\":true}}", Guid.NewGuid().ToString());
        using var second = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        await AssertStateAsync(user.Id, scheduledClass.Id, 7, 2, 3);
    }

    [TestMethod]
    public async Task RequiresAuthentication()
    {
        using var client = _host.CreateClient();
        using var response = await CreateAsync(client, 1, 1);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task SwaggerDeclaresRequiredIdempotencyHeader()
    {
        using var client = _host.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parameters = json.RootElement.GetProperty("paths").GetProperty("/enrollments")
            .GetProperty("post").GetProperty("parameters");
        var header = parameters.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.AreEqual("header", header.GetProperty("in").GetString());
        Assert.IsTrue(header.GetProperty("required").GetBoolean());
        Assert.AreEqual("bearer", json.RootElement.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty("Bearer").GetProperty("scheme").GetString());
        Assert.AreEqual(1, json.RootElement.GetProperty("paths").GetProperty("/enrollments")
            .GetProperty("post").GetProperty("security").GetArrayLength());
        Assert.IsFalse(json.RootElement.GetProperty("paths").GetProperty("/auth/login")
            .GetProperty("post").TryGetProperty("security", out _));
    }

    [TestMethod]
    public async Task MultipleIdempotencyHeaderValuesAreRejected()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        using var request = Request("{\"classId\":1,\"seats\":1}", null);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { "one", "two" });
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [DataTestMethod]
    [DataRow("{}", "key")]
    [DataRow("{\"classId\":1}", "key")]
    [DataRow("{\"seats\":1}", "key")]
    [DataRow("{\"classId\":1,\"seats\":0}", "key")]
    [DataRow("{\"classId\":1,\"seats\":-1}", "key")]
    [DataRow("{\"classId\":\"bad\",\"seats\":1}", "key")]
    [DataRow("{\"classId\":1,\"seats\":1.5}", "key")]
    [DataRow("{\"classId\":1,\"seats\":2147483648}", "key")]
    [DataRow("{", "key")]
    [DataRow("", "key")]
    [DataRow("null", "key")]
    [DataRow("{\"classId\":1,\"seats\":1}", null)]
    [DataRow("{\"classId\":1,\"seats\":1}", "")]
    [DataRow("{\"classId\":1,\"seats\":1}", "   ")]
    public async Task InvalidRequestsReturnProblemDetails(string body, string? key)
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        using var request = Request(body, key);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await WithDbAsync(async db => Assert.AreEqual(0, await db.Set<IdempotencyRecord>().CountAsync(x => x.UserId == user.Id)));
    }

    [DataTestMethod]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("pass-expired")]
    [DataRow("pass-exhausted")]
    [DataRow("capacity-exceeded")]
    public async Task BusinessRefusalsLeaveNoChanges(string reason)
    {
        var (user, scheduledClass) = await ScenarioAsync(
            visits: reason == "pass-exhausted" ? 0 : 10,
            capacity: reason == "capacity-exceeded" ? 1 : 10,
            startsAt: reason == "class-started" ? Now : Now.AddDays(1),
            cancelled: reason == "class-cancelled",
            validUntil: DateOnly.FromDateTime(reason == "pass-expired" ? Now.AddDays(-1) : Now));
        using var client = Client(user);
        using var response = await CreateAsync(client, scheduledClass.Id, 2);
        await AssertReasonAsync(response, reason);
        await AssertStateAsync(user.Id, scheduledClass.Id, reason == "pass-exhausted" ? 0 : 10, 0, 0);
        await WithDbAsync(async db => Assert.AreEqual(0, await db.Set<IdempotencyRecord>().CountAsync(x => x.UserId == user.Id)));
    }

    [TestMethod]
    public async Task UnknownNumericClassIsNotFoundAndKeyCanBeRetriedAfterRefusal()
    {
        var (user, scheduledClass) = await ScenarioAsync(capacity: 1);
        using var client = Client(user);
        using var missing = await CreateAsync(client, -1, 1);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        var key = Guid.NewGuid().ToString();
        using var refused = await CreateAsync(client, scheduledClass.Id, 2, key);
        await AssertReasonAsync(refused, "capacity-exceeded");
        await WithDbAsync(async db =>
        {
            var entity = await db.Classes.SingleAsync(x => x.Id == scheduledClass.Id);
            entity.Capacity = 2;
            await db.SaveChangesAsync();
        });
        using var retry = await CreateAsync(client, scheduledClass.Id, 2, key);
        Assert.AreEqual(HttpStatusCode.Created, retry.StatusCode);
    }

    [TestMethod]
    public async Task PassValidTodayCanBookFutureClassAndLongKeysReplay()
    {
        var (user, scheduledClass) = await ScenarioAsync(validUntil: DateOnly.FromDateTime(Now));
        using var client = Client(user);
        var key = new string('x', 10000);
        using var first = await CreateAsync(client, scheduledClass.Id, 1, key);
        using var second = await CreateAsync(client, scheduledClass.Id, 1, key);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        await AssertStateAsync(user.Id, scheduledClass.Id, 9, 1, 1);
    }

    [TestMethod]
    public async Task ParallelUsersCannotOverbookLastSeat()
    {
        var (_, scheduledClass) = await ScenarioAsync(capacity: 1);
        var users = new List<User>();
        for (var i = 0; i < 8; i++) users.Add(await AddUserAsync());
        var responses = await TogetherAsync(users.Select<User, Func<Task<HttpResponseMessage>>>(user => async () =>
        {
            using var client = Client(user);
            return await CreateAsync(client, scheduledClass.Id, 1);
        }));
        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
            await AssertReasonAsync(response, "capacity-exceeded");
        await WithDbAsync(async db =>
        {
            Assert.AreEqual(1, await db.Enrollments.CountAsync(x => x.ClassId == scheduledClass.Id));
            var ids = users.Select(x => x.Id).ToArray();
            Assert.AreEqual(79, await db.Passes.Where(x => ids.Contains(x.UserId)).SumAsync(x => x.RemainingVisits));
        });
        foreach (var response in responses) response.Dispose();
    }

    [TestMethod]
    public async Task ParallelDifferentClassesCannotSpendOneVisitTwice()
    {
        var user = await AddUserAsync(1);
        var classes = new List<Class>();
        for (var i = 0; i < 6; i++) classes.Add(await AddClassAsync());
        using var client = Client(user);
        var responses = await TogetherAsync(classes.Select<Class, Func<Task<HttpResponseMessage>>>(scheduledClass =>
            () => CreateAsync(client, scheduledClass.Id, 1)));
        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
            await AssertReasonAsync(response, "pass-exhausted");
        await WithDbAsync(async db =>
        {
            Assert.AreEqual(0, (await db.Passes.SingleAsync(x => x.UserId == user.Id)).RemainingVisits);
            Assert.AreEqual(1, await db.Enrollments.CountAsync(x => x.UserId == user.Id));
        });
        foreach (var response in responses) response.Dispose();
    }

    [TestMethod]
    public async Task ParallelSameKeyCreatesOneBookingAndIdenticalResponses()
    {
        var (user, scheduledClass) = await ScenarioAsync();
        using var client = Client(user);
        var key = Guid.NewGuid().ToString();
        var responses = await TogetherAsync(Enumerable.Range(0, 8).Select<int, Func<Task<HttpResponseMessage>>>(_ =>
            () => CreateAsync(client, scheduledClass.Id, 2, key)));
        var bodies = new List<string>();
        foreach (var response in responses)
        {
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            bodies.Add(await response.Content.ReadAsStringAsync());
            response.Dispose();
        }
        Assert.AreEqual(1, bodies.Distinct().Count());
        await AssertStateAsync(user.Id, scheduledClass.Id, 8, 1, 2);
    }

    [TestMethod]
    public async Task FailureSavingResponseRollsBackBookingPassAndKey()
    {
        var (user, scheduledClass) = await ScenarioAsync();
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(TestDatabase.PostgresConnectionString).AddInterceptors(new FailSecondSave()).Options;
        await using (var db = new ClubDbContext(options))
        {
            var service = new EnrollmentService(new UnitOfWork(db), new IdempotencyRepository(db), new FixedClock());
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.CreateAsync(user.Id, scheduledClass.Id, 2, "rollback"));
        }
        await AssertStateAsync(user.Id, scheduledClass.Id, 10, 0, 0);
        await WithDbAsync(async db => Assert.AreEqual(0, await db.Set<IdempotencyRecord>().CountAsync(x => x.UserId == user.Id)));
    }

    [TestMethod]
    public async Task SixtyRequestsAllowedThen429AndOtherUserIsIndependent()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        for (var i = 0; i < 60; i++)
        {
            using var response = await CreateAsync(client, -1, 1);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var limited = await CreateAsync(client, -1, 1);
        Assert.AreEqual((HttpStatusCode)429, limited.StatusCode);
        var other = await AddUserAsync();
        using var otherClient = Client(other);
        using var independent = await CreateAsync(otherClient, -1, 1);
        Assert.AreEqual(HttpStatusCode.NotFound, independent.StatusCode);

        var redis = _host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var key = $"fitness:requests:{user.Id}";
        await redis.KeyDeleteAsync(key);
        await redis.SortedSetAddAsync(key, "expired", 0);
        using var afterExpiry = await CreateAsync(client, -1, 1);
        Assert.AreEqual(HttpStatusCode.NotFound, afterExpiry.StatusCode);
        Assert.AreEqual(1L, await redis.SortedSetLengthAsync(key));
    }

    [TestMethod]
    public async Task LimiterAtomicallyAdmitsExactlySixtyConcurrentCalls()
    {
        var user = await AddUserAsync();
        var limiter = _host.Services.GetRequiredService<EnrollmentRateLimiter>();
        var results = await Task.WhenAll(Enumerable.Range(0, 80).Select(_ => limiter.TryAcquireAsync(user.Id)));
        Assert.AreEqual(60, results.Count(x => x));
    }

    [TestMethod]
    public async Task WaitingRequestReadsCommittedPassBalanceAfterLockIsReleased()
    {
        var (user, scheduledClass) = await ScenarioAsync(visits: 1);
        await using var blocker = new ClubDbContext(new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(TestDatabase.PostgresConnectionString).Options);
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE passes SET remaining_visits = 0 WHERE user_id = {user.Id}");
        using var client = Client(user);
        var pending = CreateAsync(client, scheduledClass.Id, 1);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        var waiting = false;
        await using var observer = new Npgsql.NpgsqlConnection(TestDatabase.PostgresConnectionString);
        await observer.OpenAsync();
        while (!waiting && DateTime.UtcNow < deadline)
        {
            await using var command = observer.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock'
                AND query LIKE '%passes%' AND query LIKE '%FOR UPDATE%')
                """;
            waiting = (bool)(await command.ExecuteScalarAsync())!;
            if (!waiting) await Task.Delay(20);
        }
        await transaction.CommitAsync();
        using var response = await pending;
        Assert.IsTrue(waiting, "The request must actually wait on PostgreSQL's row lock");
        await AssertReasonAsync(response, "pass-exhausted");
        await AssertStateAsync(user.Id, scheduledClass.Id, 0, 0, 0);
    }

    [TestMethod]
    public async Task CountsOnlyActiveSeatsAcrossUsers()
    {
        var (user, scheduledClass) = await ScenarioAsync(capacity: 3);
        var other = await AddUserAsync();
        await WithDbAsync(async db =>
        {
            db.Enrollments.AddRange(
                new Enrollment { UserId = other.Id, ClassId = scheduledClass.Id, Seats = 2, Status = "active", CreatedAt = Now },
                new Enrollment { UserId = user.Id, ClassId = scheduledClass.Id, Seats = 9, Status = "cancelled", CreatedAt = Now });
            await db.SaveChangesAsync();
        });
        using var client = Client(user);
        using var fits = await CreateAsync(client, scheduledClass.Id, 1);
        Assert.AreEqual(HttpStatusCode.Created, fits.StatusCode);
        using var full = await CreateAsync(client, scheduledClass.Id, 1);
        await AssertReasonAsync(full, "capacity-exceeded");
    }

    [TestMethod]
    public async Task EquivalentJsonReplaysAndDifferentClassConflicts()
    {
        var (user, scheduledClass) = await ScenarioAsync();
        var otherClass = await AddClassAsync();
        using var client = Client(user);
        var key = Guid.NewGuid().ToString();
        using var first = await CreateAsync(client, scheduledClass.Id, 1, key);
        using var request = Request($"{{\"ignored\":true,\"seats\":1,\"classId\":{scheduledClass.Id}}}", key);
        using var replay = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var conflict = await CreateAsync(client, otherClass.Id, 1, key);
        await AssertReasonAsync(conflict, "idempotency-key-reused");
    }

    [TestMethod]
    public async Task UpgradeFromSeededInitialSchemaPreservesAllOriginalRows()
    {
        var databaseName = $"club_upgrade_{Guid.NewGuid():N}";
        var server = new Npgsql.NpgsqlConnectionStringBuilder(TestDatabase.PostgresConnectionString) { Database = "postgres", Pooling = false };
        await using var connection = new Npgsql.NpgsqlConnection(server.ConnectionString);
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var settings = new Npgsql.NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = databaseName };
            await using var db = new ClubDbContext(new DbContextOptionsBuilder<ClubDbContext>().UseNpgsql(settings.ConnectionString).Options);
            var migrator = db.GetService<IMigrator>();
            await db.Database.MigrateAsync();
            await DatabaseSeeder.SeedAsync(db);
            await migrator.MigrateAsync(db.Database.GetMigrations().First());
            var before = await OriginalDataAsync(db);
            await db.Database.MigrateAsync();
            var after = await OriginalDataAsync(db);
            Assert.AreEqual(before, after);
            var historical = await (
                from enrollment in db.Enrollments.AsNoTracking()
                join scheduledClass in db.Classes.IgnoreQueryFilters().AsNoTracking() on enrollment.ClassId equals scheduledClass.Id
                select new { enrollment.Status, enrollment.Seats, enrollment.CancelledAt, enrollment.RefundedVisits,
                    scheduledClass.IsCancelled, scheduledClass.StartsAt }).ToListAsync();
            foreach (var entry in historical)
            {
                var expected = entry.Status == "cancelled"
                    && (entry.IsCancelled || entry.CancelledAt <= entry.StartsAt.AddHours(-2)) ? entry.Seats : 0;
                Assert.AreEqual(expected, entry.RefundedVisits);
            }
            Assert.AreEqual(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.IsFalse(await db.Set<IdempotencyRecord>().AnyAsync());
            Assert.IsFalse(db.Database.HasPendingModelChanges());
        }
        finally
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> OriginalDataAsync(ClubDbContext db)
    {
        return JsonSerializer.Serialize(new
        {
            Users = await db.Users.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Rooms = await db.Rooms.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Classes = await db.Classes.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.RoomId, x.Title, x.StartsAt, x.DurationMinutes, x.Capacity, x.IsCancelled }).ToListAsync(),
            Passes = await db.Passes.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Enrollments = await db.Enrollments.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.UserId, x.ClassId, x.Seats, x.Status, x.CreatedAt, x.CancelledAt }).ToListAsync()
        });
    }

    private static async Task<HttpResponseMessage[]> TogetherAsync(IEnumerable<Func<Task<HttpResponseMessage>>> operations)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(async operation => { await start.Task; return await operation(); }).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static HttpClient Client(User user)
    {
        var client = _host.CreateClient();
        var token = _host.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpRequestMessage Request(string body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return request;
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, long classId, int seats, string? key = null)
    {
        using var request = Request(JsonSerializer.Serialize(new { classId, seats }), key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    private static async Task AssertReasonAsync(HttpResponseMessage response, string reason)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, content);
        using var json = JsonDocument.Parse(content);
        Assert.AreEqual(409, json.RootElement.GetProperty("status").GetInt32());
        Assert.AreEqual(reason, json.RootElement.GetProperty("reason").GetString());
    }

    private static async Task AssertStateAsync(long userId, long classId, int visits, int count, int seats)
    {
        await WithDbAsync(async db =>
        {
            Assert.AreEqual(visits, (await db.Passes.SingleAsync(x => x.UserId == userId)).RemainingVisits);
            var enrollments = await db.Enrollments.Where(x => x.UserId == userId && x.ClassId == classId).ToListAsync();
            Assert.AreEqual(count, enrollments.Count);
            Assert.AreEqual(seats, enrollments.Sum(x => x.Seats));
        });
    }

    private static async Task WithDbAsync(Func<ClubDbContext, Task> action)
    {
        using var scope = _host.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ClubDbContext>());
    }

    private static async Task<User> AddUserAsync(int visits = 10, DateOnly? validUntil = null)
    {
        var user = new User { Email = $"{Guid.NewGuid():N}@test", PasswordHash = "unused", Role = "user", CreatedAt = Now };
        await WithDbAsync(async db =>
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.Passes.Add(new Pass { UserId = user.Id, RemainingVisits = visits, ValidUntil = validUntil ?? DateOnly.FromDateTime(Now.AddDays(30)) });
            await db.SaveChangesAsync();
        });
        return user;
    }

    private static async Task<Class> AddClassAsync(int capacity = 10, DateTime? startsAt = null, bool cancelled = false)
    {
        var scheduledClass = new Class
        {
            Title = "Test class", Capacity = capacity, StartsAt = startsAt ?? Now.AddDays(1),
            DurationMinutes = 60, IsCancelled = cancelled
        };
        await WithDbAsync(async db =>
        {
            scheduledClass.RoomId = await db.Rooms.Select(x => x.Id).FirstAsync();
            db.Classes.Add(scheduledClass);
            await db.SaveChangesAsync();
        });
        return scheduledClass;
    }

    private static async Task<(User, Class)> ScenarioAsync(int visits = 10, int capacity = 10,
        DateTime? startsAt = null, bool cancelled = false, DateOnly? validUntil = null)
    {
        return (await AddUserAsync(visits, validUntil), await AddClassAsync(capacity, startsAt, cancelled));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private sealed class FailSecondSave : SaveChangesInterceptor
    {
        private int _calls;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (++_calls == 2) throw new InvalidOperationException("Simulated response storage failure");
            return ValueTask.FromResult(result);
        }
    }
}
