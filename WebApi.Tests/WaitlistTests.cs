using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Enrollments;

namespace WebApi.Tests;

public partial class EnrollmentCreationTests
{
    [TestMethod]
    public async Task JoinWaitlistStoresEntryWithoutChargingVisitsAndReturnsPosition()
    {
        var (user, scheduledClass) = await ScenarioAsync(visits: 7, capacity: 3);
        var secondUser = await AddUserAsync(visits: 7);
        using var client = Client(user);
        using var secondClient = Client(secondUser);
        using var fill = await CreateAsync(client, scheduledClass.Id, 3);
        Assert.AreEqual(HttpStatusCode.Created, fill.StatusCode);
        var before = await PassBalanceAsync(user.Id);

        using var first = await JoinAsync(client, scheduledClass.Id, 2);
        using var second = await JoinAsync(secondClient, scheduledClass.Id, 3);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode, await second.Content.ReadAsStringAsync());
        var firstResult = await first.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        var secondResult = await second.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        Assert.AreEqual(1, firstResult!.Position);
        Assert.AreEqual(2, secondResult!.Position);
        Assert.AreEqual(2, firstResult.Seats);
        Assert.AreEqual(before, await PassBalanceAsync(user.Id));
        await WithDbAsync(async db =>
        {
            Assert.AreEqual(1, await db.WaitlistEntries.CountAsync(x => x.UserId == user.Id && x.Status == "waiting"));
            Assert.AreEqual(1, await db.WaitlistEntries.CountAsync(x => x.UserId == secondUser.Id && x.Status == "waiting"));
            Assert.AreEqual(3, await db.Enrollments.Where(x => x.UserId == user.Id && x.Status == "active")
                .SumAsync(x => x.Seats));
        });
    }

    [TestMethod]
    public async Task JoinWaitlistReplaysInitialPositionAndConflictsOnDifferentPayload()
    {
        var (user, scheduledClass) = await FullClassAsync(3);
        var secondUser = await AddUserAsync();
        using var client = Client(user);
        using var secondClient = Client(secondUser);
        var key = Guid.NewGuid().ToString();
        using var first = await JoinAsync(client, scheduledClass.Id, 2, key);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        var original = await first.Content.ReadAsStringAsync();
        var firstResult = await first.Content.ReadFromJsonAsync<JoinWaitlistResponse>();

        using var second = await JoinAsync(secondClient, scheduledClass.Id, 1);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode, await second.Content.ReadAsStringAsync());
        using var replay = await JoinAsync(client, scheduledClass.Id, 2, key);
        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
        Assert.AreEqual(original, await replay.Content.ReadAsStringAsync());
        using var conflict = await JoinAsync(client, scheduledClass.Id, 3, key);
        await AssertReasonAsync(conflict, "idempotency-key-reused");
        Assert.AreEqual(1, firstResult!.Position);
        await WithDbAsync(async db =>
        {
            Assert.AreEqual(1, await db.WaitlistEntries.CountAsync(x => x.UserId == user.Id && x.Status == "waiting"));
            Assert.AreEqual(1, await db.WaitlistEntries.CountAsync(x => x.UserId == secondUser.Id && x.Status == "waiting"));
        });
    }

    [TestMethod]
    public async Task JoinWaitlistSameKeyIsIndependentAcrossUsersAndOperations()
    {
        var (firstUser, scheduledClass) = await FullClassAsync(2);
        var secondUser = await AddUserAsync();
        using var firstClient = Client(firstUser);
        using var secondClient = Client(secondUser);
        var key = Guid.NewGuid().ToString();
        using var first = await JoinAsync(firstClient, scheduledClass.Id, 2, key);
        using var otherUser = await JoinAsync(secondClient, scheduledClass.Id, 2, key);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, otherUser.StatusCode);
        using var enrollmentOperation = await CreateAsync(firstClient, scheduledClass.Id, 1, key);
        await AssertReasonAsync(enrollmentOperation, "capacity-exceeded");
    }

    [TestMethod]
    public async Task OneUserCannotWaitTwiceAndAvailableCapacityIsRejected()
    {
        var (user, scheduledClass) = await FullClassAsync(3);
        using var client = Client(user);
        using var first = await JoinAsync(client, scheduledClass.Id, 1);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await JoinAsync(client, scheduledClass.Id, 2);
        await AssertReasonAsync(duplicate, "already-waiting");
        var open = await AddClassAsync(capacity: 5);
        using var direct = await CreateAsync(client, open.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, direct.StatusCode);
        using var unnecessaryWait = await JoinAsync(client, open.Id, 2);
        await AssertReasonAsync(unnecessaryWait, "seats-available");
    }

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("{\"classId\":1}")]
    [DataRow("{\"seats\":1}")]
    [DataRow("{\"classId\":1,\"seats\":0}")]
    [DataRow("{\"classId\":1,\"seats\":-1}")]
    [DataRow("{\"classId\":\"bad\",\"seats\":1}")]
    [DataRow("{")]
    [DataRow("")]
    [DataRow("null")]
    public async Task JoinWaitlistValidatesBodyAndIdempotencyHeader(string body)
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        using var request = WaitlistRequest(body, null);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await WithDbAsync(async db => Assert.AreEqual(0,
            await db.Set<DataAccessLayer.Entities.IdempotencyRecord>().CountAsync(x => x.UserId == user.Id)));
    }

    [TestMethod]
    public async Task JoinWaitlistRequiresAuthenticationAndHandlesMissingClasses()
    {
        using var anonymous = _host.CreateClient();
        using var unauthorized = await JoinAsync(anonymous, 1, 1);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var user = await AddUserAsync();
        using var client = Client(user);
        using var missing = await JoinAsync(client, -1, 1);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        var cancelled = await AddClassAsync(cancelled: true);
        using var refused = await JoinAsync(client, cancelled.Id, 1);
        await AssertReasonAsync(refused, "class-cancelled");
        var started = await AddClassAsync(startsAt: Now);
        using var startedResponse = await JoinAsync(client, started.Id, 1);
        await AssertReasonAsync(startedResponse, "class-started");
    }

    [TestMethod]
    public async Task CancellingWaitingEntryMovesPositionsAndCanBeRepeated()
    {
        var userA = await AddUserAsync();
        var userB = await AddUserAsync();
        var userC = await AddUserAsync();
        var scheduledClass = await AddClassAsync(capacity: 1);
        using var fill = Client(await AddUserAsync());
        using var booked = await CreateAsync(fill, scheduledClass.Id, 1);
        using var clientA = Client(userA);
        using var clientB = Client(userB);
        using var clientC = Client(userC);
        using var joinedA = await JoinAsync(clientA, scheduledClass.Id, 1);
        using var joinedB = await JoinAsync(clientB, scheduledClass.Id, 1);
        using var joinedC = await JoinAsync(clientC, scheduledClass.Id, 1);
        var a = await joinedA.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        var b = await joinedB.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        var c = await joinedC.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        Assert.AreEqual(1, a!.Position);
        Assert.AreEqual(2, b!.Position);
        Assert.AreEqual(3, c!.Position);

        using var removed = await clientB.PostAsync($"/enrollments/waitlist/{b.Id}/cancel", null);
        using var repeated = await clientB.PostAsync($"/enrollments/waitlist/{b.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
        Assert.AreEqual(await removed.Content.ReadAsStringAsync(), await repeated.Content.ReadAsStringAsync());
        using var next = await JoinAsync(Client(await AddUserAsync()), scheduledClass.Id, 1);
        var nextEntry = await next.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        Assert.AreEqual(3, nextEntry!.Position);
        await WithDbAsync(async db => Assert.AreEqual(3,
            await db.WaitlistEntries.CountAsync(x => x.ClassId == scheduledClass.Id && x.Status == "waiting")));
    }

    [TestMethod]
    public async Task CancellingFulfilledEntryReturnsConflictAndForeignIdsStayHidden()
    {
        var (owner, scheduledClass) = await FullClassAsync(1);
        using var ownerClient = Client(owner);
        using var joined = await JoinAsync(ownerClient, scheduledClass.Id, 1);
        var entry = await joined.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        var stranger = await AddUserAsync();
        using var strangerClient = Client(stranger);
        using var foreign = await strangerClient.PostAsync($"/enrollments/waitlist/{entry!.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        using var booking = await ownerClient.PostAsync($"/enrollments/{(await FillEnrollmentIdAsync(owner.Id, scheduledClass.Id))}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, booking.StatusCode, await booking.Content.ReadAsStringAsync());
        using var fulfilled = await ownerClient.PostAsync($"/enrollments/waitlist/{entry.Id}/cancel", null);
        await AssertReasonAsync(fulfilled, "waitlist-fulfilled");
        using var missing = await ownerClient.PostAsync("/enrollments/waitlist/-1/cancel", null);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        using var malformed = await ownerClient.PostAsync("/enrollments/waitlist/bad/cancel", null);
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [TestMethod]
    public async Task CancellationPromotesWaitingEntriesInFifoAndChargesPassesOnce()
    {
        var (owner, scheduledClass, _) = await BookedScenarioAsync(capacity: 3, seats: 3);
        var waiter1 = await AddUserAsync();
        var waiter2 = await AddUserAsync();
        using var client1 = Client(waiter1);
        using var client2 = Client(waiter2);
        var idempotencyKey = Guid.NewGuid().ToString();
        using var first = await JoinAsync(client1, scheduledClass.Id, 2, idempotencyKey);
        using var second = await JoinAsync(client2, scheduledClass.Id, 1);
        using var ownerClient = Client(owner);
        var enrollment = await EnrollmentForUserAsync(owner.Id, scheduledClass.Id);

        using var cancelled = await ownerClient.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode, await cancelled.Content.ReadAsStringAsync());
        await AssertStateAsync(owner.Id, scheduledClass.Id, 10, 1, 3);
        await WithDbAsync(async db =>
        {
            var entries = await db.WaitlistEntries.Where(x => x.ClassId == scheduledClass.Id)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync();
            Assert.AreEqual("fulfilled", entries[0].Status);
            Assert.AreEqual("fulfilled", entries[1].Status);
            Assert.IsNotNull(entries[0].EnrollmentId);
            Assert.IsNotNull(entries[1].EnrollmentId);
            Assert.AreEqual(8, (await db.Passes.SingleAsync(x => x.UserId == waiter1.Id)).RemainingVisits);
            Assert.AreEqual(9, (await db.Passes.SingleAsync(x => x.UserId == waiter2.Id)).RemainingVisits);
            Assert.AreEqual(3L, await new EnrollmentRepository(db.Enrollments).GetBookedSeatsAsync(scheduledClass.Id));
        });
        using var replayFirst = await JoinAsync(client1, scheduledClass.Id, 2, idempotencyKey);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await replayFirst.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task StrictFifoLeavesLaterSmallerRequestWaiting()
    {
        var (owner, scheduledClass, _) = await BookedScenarioAsync(capacity: 5, seats: 2);
        var otherOwner = await AddUserAsync();
        using var otherOwnerClient = Client(otherOwner);
        using var otherBooking = await CreateAsync(otherOwnerClient, scheduledClass.Id, 3);
        var waiter1 = await AddUserAsync();
        var waiter2 = await AddUserAsync();
        using var client1 = Client(waiter1);
        using var client2 = Client(waiter2);
        using var tooLargeForCurrentSpace = await JoinAsync(client1, scheduledClass.Id, 3);
        using var fitsLater = await JoinAsync(client2, scheduledClass.Id, 1);
        await WithDbAsync(async db =>
        {
        });
        using var ownerClient = Client(owner);
        var enrollment = await EnrollmentForUserAsync(owner.Id, scheduledClass.Id);
        using var cancelled = await ownerClient.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode, await cancelled.Content.ReadAsStringAsync());
        await WithDbAsync(async db =>
        {
            var entries = await db.WaitlistEntries.Where(x => x.ClassId == scheduledClass.Id)
                .OrderBy(x => x.Id).ToListAsync();
            Assert.AreEqual("waiting", entries[0].Status);
            Assert.AreEqual("waiting", entries[1].Status);
            Assert.AreEqual(1, await db.Enrollments.CountAsync(x => x.ClassId == scheduledClass.Id && x.Status == "active"));
        });
    }

    [TestMethod]
    public async Task InvalidPassIsSkippedSoNextEligibleEntryGetsPlace()
    {
        var (owner, scheduledClass, _) = await BookedScenarioAsync(capacity: 2, seats: 2);
        var expired = await AddUserAsync(validUntil: DateOnly.FromDateTime(Now.AddDays(-1)));
        var eligible = await AddUserAsync();
        using var expiredClient = Client(expired);
        using var eligibleClient = Client(eligible);
        using var skipped = await JoinAsync(expiredClient, scheduledClass.Id, 1);
        using var next = await JoinAsync(eligibleClient, scheduledClass.Id, 1);
        using var ownerClient = Client(owner);
        var enrollment = await EnrollmentForUserAsync(owner.Id, scheduledClass.Id);
        using var cancelled = await ownerClient.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        await WithDbAsync(async db =>
        {
            var entries = await db.WaitlistEntries.Where(x => x.ClassId == scheduledClass.Id).OrderBy(x => x.Id).ToListAsync();
            Assert.AreEqual("cancelled", entries[0].Status);
            Assert.AreEqual("pass-expired", entries[0].CompletionReason);
            Assert.AreEqual("fulfilled", entries[1].Status);
            Assert.AreEqual(10, (await db.Passes.SingleAsync(x => x.UserId == expired.Id)).RemainingVisits);
            Assert.AreEqual(9, (await db.Passes.SingleAsync(x => x.UserId == eligible.Id)).RemainingVisits);
        });
    }

    [TestMethod]
    public async Task RequestLargerThanFullClassIsRetiredAndNextEntryPromoted()
    {
        var (owner, scheduledClass, _) = await BookedScenarioAsync(capacity: 3, seats: 3);
        var impossible = await AddUserAsync();
        var eligible = await AddUserAsync();
        using var impossibleClient = Client(impossible);
        using var eligibleClient = Client(eligible);
        using var oversized = await JoinAsync(impossibleClient, scheduledClass.Id, 4);
        using var next = await JoinAsync(eligibleClient, scheduledClass.Id, 1);
        Assert.AreEqual(HttpStatusCode.Created, oversized.StatusCode);
        using var ownerClient = Client(owner);
        var enrollment = await EnrollmentForUserAsync(owner.Id, scheduledClass.Id);
        using var cancel = await ownerClient.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode, await cancel.Content.ReadAsStringAsync());
        await WithDbAsync(async db =>
        {
            var entries = await db.WaitlistEntries.Where(x => x.ClassId == scheduledClass.Id).OrderBy(x => x.Id).ToListAsync();
            Assert.AreEqual("cancelled", entries[0].Status);
            Assert.AreEqual("seats-exceed-capacity", entries[0].CompletionReason);
            Assert.AreEqual("fulfilled", entries[1].Status);
        });
    }

    [TestMethod]
    public async Task DecreasingBookingPromotesQueueImmediately()
    {
        var (owner, scheduledClass, booking) = await BookedScenarioAsync(capacity: 3, seats: 3);
        var waiter = await AddUserAsync();
        using var waiterClient = Client(waiter);
        using var waiting = await JoinAsync(waiterClient, scheduledClass.Id, 2);
        using var ownerClient = Client(owner);
        using var changed = await ownerClient.PostAsJsonAsync($"/enrollments/{booking.Id}/seats", new { seats = 1 });
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        await AssertStateAsync(owner.Id, scheduledClass.Id, 9, 1, 1);
        await WithDbAsync(async db =>
        {
            Assert.AreEqual("fulfilled", (await db.WaitlistEntries.SingleAsync(x => x.ClassId == scheduledClass.Id)).Status);
            Assert.AreEqual(8, (await db.Passes.SingleAsync(x => x.UserId == waiter.Id)).RemainingVisits);
        });
    }

    [TestMethod]
    public async Task ConcurrentSameUserWaitlistEntriesCreateOnlyOneWaitingRow()
    {
        var (user, scheduledClass) = await FullClassAsync(1);
        using var client = Client(user);
        var results = await TogetherAsync(Enumerable.Range(0, 2).Select<int, Func<Task<HttpResponseMessage>>>(_ =>
            () => JoinAsync(client, scheduledClass.Id, 1)));
        Assert.AreEqual(1, results.Count(x => x.StatusCode == HttpStatusCode.Created));
        await AssertReasonAsync(results.Single(x => x.StatusCode != HttpStatusCode.Created), "already-waiting");
        foreach (var response in results) response.Dispose();
        await WithDbAsync(async db => Assert.AreEqual(1,
            await db.WaitlistEntries.CountAsync(x => x.UserId == user.Id && x.ClassId == scheduledClass.Id && x.Status == "waiting")));
    }

    [TestMethod]
    public async Task ConcurrentDuplicateIdempotencyKeyWaitlistReplaysOneEntry()
    {
        var (user, scheduledClass) = await FullClassAsync(1);
        using var client = Client(user);
        var key = Guid.NewGuid().ToString();
        var results = await TogetherAsync(Enumerable.Range(0, 4).Select<int, Func<Task<HttpResponseMessage>>>(_ =>
            () => JoinAsync(client, scheduledClass.Id, 1, key)));
        Assert.IsTrue(results.All(x => x.StatusCode == HttpStatusCode.Created));
        var bodies = new List<string>();
        foreach (var response in results)
        {
            bodies.Add(await response.Content.ReadAsStringAsync());
            response.Dispose();
        }
        Assert.AreEqual(1, bodies.Distinct().Count());
        await WithDbAsync(async db => Assert.AreEqual(1,
            await db.WaitlistEntries.CountAsync(x => x.UserId == user.Id && x.ClassId == scheduledClass.Id)));
    }

    [TestMethod]
    public async Task WaitlistAndEnrollmentRoutesShareSameRateLimit()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        for (var i = 0; i < 60; i++)
        {
            using var response = i % 2 == 0
                ? await JoinAsync(client, -1, 1)
                : await CreateAsync(client, -1, 1);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var limited = await JoinAsync(client, -1, 1);
        Assert.AreEqual((HttpStatusCode)429, limited.StatusCode);
    }

    [TestMethod]
    public async Task WaitlistPromotionSaveFailureRollsBackCancellationAndAllPromotedBookings()
    {
        var (owner, scheduledClass, booking) = await BookedScenarioAsync(capacity: 2, seats: 2);
        var waiter = await AddUserAsync();
        using var waiterClient = Client(waiter);
        using var waiting = await JoinAsync(waiterClient, scheduledClass.Id, 1);
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(TestDatabase.PostgresConnectionString).AddInterceptors(new FailLifecycleSave()).Options;
        await using (var db = new ClubDbContext(options))
        {
            var service = new WebApi.Enrollments.EnrollmentService(new DataAccessLayer.UnitOfWork.UnitOfWork(db),
                new IdempotencyRepository(db), new FixedClock());
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.CancelAsync(owner.Id, booking.Id));
        }
        await AssertStateAsync(owner.Id, scheduledClass.Id, 8, 1, 2);
        await AssertStateAsync(waiter.Id, scheduledClass.Id, 10, 0, 0);
        await WithDbAsync(async db => Assert.AreEqual("waiting",
            (await db.WaitlistEntries.SingleAsync(x => x.UserId == waiter.Id)).Status));
    }

    private static async Task<(User User, Class Class)> FullClassAsync(int capacity)
    {
        var (user, scheduledClass) = await ScenarioAsync(capacity: capacity);
        using var client = Client(user);
        using var response = await CreateAsync(client, scheduledClass.Id, capacity);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (user, scheduledClass);
    }

    private static async Task<long> FillEnrollmentIdAsync(long userId, long classId) =>
        (await EnrollmentForUserAsync(userId, classId)).Id;

    private static async Task<CreateEnrollmentResponse> EnrollmentForUserAsync(long userId, long classId)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var enrollment = await db.Enrollments.AsNoTracking()
            .SingleAsync(x => x.UserId == userId && x.ClassId == classId && x.Status == "active");
        return new CreateEnrollmentResponse(enrollment.Id, enrollment.ClassId, enrollment.Seats,
            enrollment.Status, enrollment.CreatedAt);
    }

    private static async Task<int> PassBalanceAsync(long userId)
    {
        var result = 0;
        await WithDbAsync(async db => result = (await db.Passes.SingleAsync(x => x.UserId == userId)).RemainingVisits);
        return result;
    }

    private static async Task<HttpResponseMessage> JoinAsync(
        HttpClient client, long classId, int seats, string? key = null)
    {
        using var request = WaitlistRequest(JsonSerializer.Serialize(new { classId, seats }), key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage WaitlistRequest(string body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return request;
    }
}
