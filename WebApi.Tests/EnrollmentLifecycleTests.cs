using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WebApi.Enrollments;

namespace WebApi.Tests;

public partial class EnrollmentCreationTests
{
    [TestMethod]
    public async Task CardHasExactNestedContractAndCurrentPass()
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        using var client = Client(user);
        using var card = await client.GetAsync($"/enrollments/{enrollment.Id}");
        Assert.AreEqual(HttpStatusCode.OK, card.StatusCode);
        using var json = JsonDocument.Parse(await card.Content.ReadAsStringAsync());
        AssertFields(json.RootElement, "id", "status", "seats", "createdAt", "class", "pass");
        var classJson = json.RootElement.GetProperty("class");
        AssertFields(classJson, "id", "title", "startsAt", "durationMinutes", "isCancelled", "room");
        AssertFields(classJson.GetProperty("room"), "id", "name");
        var pass = json.RootElement.GetProperty("pass");
        AssertFields(pass, "remainingVisits", "validUntil");
        Assert.AreEqual(8, pass.GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual("2030-05-15", pass.GetProperty("validUntil").GetString());
        Assert.AreEqual(scheduledClass.Id, classJson.GetProperty("id").GetInt64());

        using var otherBooking = await CreateAsync(client, scheduledClass.Id, 1);
        Assert.AreEqual(HttpStatusCode.Created, otherBooking.StatusCode);
        using var updated = await client.GetAsync($"/enrollments/{enrollment.Id}");
        var details = await updated.Content.ReadFromJsonAsync<EnrollmentCardResponse>();
        Assert.AreEqual(7, details!.Pass.RemainingVisits);
    }

    [TestMethod]
    public async Task CardShowsCancelledBookingAndCancelledClass()
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        await WithDbAsync(async db =>
        {
            var entity = await db.Classes.SingleAsync(x => x.Id == scheduledClass.Id);
            entity.IsCancelled = true;
            var booking = await db.Enrollments.SingleAsync(x => x.Id == enrollment.Id);
            booking.Status = "cancelled";
            await db.SaveChangesAsync();
        });
        using var client = Client(user);
        using var response = await client.GetAsync($"/enrollments/{enrollment.Id}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var card = await response.Content.ReadFromJsonAsync<EnrollmentCardResponse>();
        Assert.AreEqual("cancelled", card!.Status);
        Assert.IsTrue(card.Class.IsCancelled);
        Assert.AreEqual(2, card.Seats);
    }

    [DataTestMethod]
    [DataRow("card")]
    [DataRow("seats")]
    [DataRow("cancel")]
    public async Task LifecycleRoutesHideForeignAndMissingBookings(string operation)
    {
        var (_, _, enrollment) = await BookedScenarioAsync();
        using var client = Client(await AddUserAsync());
        using var foreign = await LifecycleAsync(client, enrollment.Id.ToString(), operation);
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        using var missing = await LifecycleAsync(client, "-1", operation);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        using var malformed = await LifecycleAsync(client, "bad", operation);
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var overflow = await LifecycleAsync(client, "999999999999999999999999999", operation);
        Assert.AreEqual(HttpStatusCode.BadRequest, overflow.StatusCode);
        using var anonymous = _host.CreateClient();
        using var unauthenticated = await LifecycleAsync(anonymous, enrollment.Id.ToString(), operation);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [TestMethod]
    public async Task IncreaseAndDecreaseReturnFullCardAndCorrectBalance()
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        using var client = Client(user);
        using var increase = await client.PostAsJsonAsync($"/enrollments/{enrollment.Id}/seats", new { seats = 5 });
        Assert.AreEqual(HttpStatusCode.OK, increase.StatusCode, await increase.Content.ReadAsStringAsync());
        var card = await increase.Content.ReadFromJsonAsync<EnrollmentCardResponse>();
        Assert.AreEqual(5, card!.Seats);
        Assert.AreEqual(5, card.Pass.RemainingVisits);
        Assert.AreEqual(scheduledClass.Id, card.Class.Id);
        Assert.IsFalse(string.IsNullOrEmpty(card.Class.Room.Name));
        using var decrease = await client.PostAsJsonAsync($"/enrollments/{enrollment.Id}/seats", new { seats = 1, ignored = true });
        Assert.AreEqual(HttpStatusCode.OK, decrease.StatusCode);
        var smaller = await decrease.Content.ReadFromJsonAsync<EnrollmentCardResponse>();
        Assert.AreEqual(9, smaller!.Pass.RemainingVisits);
        Assert.AreEqual(1, smaller.Seats);
        await AssertStateAsync(user.Id, scheduledClass.Id, 9, 1, 1);
    }

    [TestMethod]
    public async Task DecreaseAndNoOpWorkWithExpiredExhaustedPass()
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        await WithDbAsync(async db =>
        {
            var pass = await db.Passes.SingleAsync(x => x.UserId == user.Id);
            pass.ValidUntil = DateOnly.FromDateTime(Now.AddDays(-1));
            pass.RemainingVisits = 0;
            await db.SaveChangesAsync();
        });
        using var client = Client(user);
        using var same = await client.PostAsJsonAsync($"/enrollments/{enrollment.Id}/seats", new { seats = 2 });
        Assert.AreEqual(HttpStatusCode.OK, same.StatusCode);
        await AssertStateAsync(user.Id, scheduledClass.Id, 0, 1, 2);
        using var decrease = await client.PostAsJsonAsync($"/enrollments/{enrollment.Id}/seats", new { seats = 1 });
        Assert.AreEqual(HttpStatusCode.OK, decrease.StatusCode);
        await AssertStateAsync(user.Id, scheduledClass.Id, 1, 1, 1);
    }

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("{\"seats\":0}")]
    [DataRow("{\"seats\":-1}")]
    [DataRow("{\"seats\":\"bad\"}")]
    [DataRow("{\"seats\":1.2}")]
    [DataRow("{")]
    [DataRow("")]
    [DataRow("null")]
    public async Task ChangeSeatsValidatesBodyWithoutChangingState(string body)
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        using var client = Client(user);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/enrollments/{enrollment.Id}/seats", content);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertStateAsync(user.Id, scheduledClass.Id, 8, 1, 2);
    }

    [DataTestMethod]
    [DataRow("enrollment-cancelled", 2)]
    [DataRow("enrollment-cancelled", 3)]
    [DataRow("class-cancelled", 2)]
    [DataRow("class-cancelled", 1)]
    [DataRow("class-started", 2)]
    [DataRow("class-started", 1)]
    [DataRow("pass-expired", 3)]
    [DataRow("pass-exhausted", 3)]
    [DataRow("capacity-exceeded", 3)]
    public async Task ChangeSeatsRefusalsLeaveStateUnchanged(string reason, int seats)
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        await WithDbAsync(async db =>
        {
            var booking = await db.Enrollments.SingleAsync(x => x.Id == enrollment.Id);
            var entity = await db.Classes.SingleAsync(x => x.Id == scheduledClass.Id);
            var pass = await db.Passes.SingleAsync(x => x.UserId == user.Id);
            if (reason == "enrollment-cancelled") booking.Status = "cancelled";
            if (reason == "class-cancelled") entity.IsCancelled = true;
            if (reason == "class-started") entity.StartsAt = Now;
            if (reason == "pass-expired") pass.ValidUntil = DateOnly.FromDateTime(Now.AddDays(-1));
            if (reason == "pass-exhausted") pass.RemainingVisits = 0;
            if (reason == "capacity-exceeded") entity.Capacity = 2;
            await db.SaveChangesAsync();
        });
        using var client = Client(user);
        using var response = await client.PostAsJsonAsync($"/enrollments/{enrollment.Id}/seats", new { seats });
        await AssertReasonAsync(response, reason);
        await AssertStateAsync(user.Id, scheduledClass.Id, reason == "pass-exhausted" ? 0 : 8, 1, 2);
    }

    [DataTestMethod]
    [DataRow(120, false, 2)]
    [DataRow(121, false, 2)]
    [DataRow(119, false, 0)]
    [DataRow(1, false, 0)]
    [DataRow(0, true, 2)]
    [DataRow(-1, true, 2)]
    [DataRow(119, true, 2)]
    public async Task CancellationHonorsRefundBoundaryAndClubCancellation(int minutes, bool cancelled, int refund)
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        await WithDbAsync(async db =>
        {
            var entity = await db.Classes.SingleAsync(x => x.Id == scheduledClass.Id);
            entity.StartsAt = Now.AddMinutes(minutes);
            entity.IsCancelled = cancelled;
            await db.SaveChangesAsync();
        });
        using var client = Client(user);
        using var first = await client.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        var original = await first.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(original);
        AssertFields(json.RootElement, "id", "status", "refundedVisits");
        Assert.AreEqual(refund, json.RootElement.GetProperty("refundedVisits").GetInt32());
        await AssertStateAsync(user.Id, scheduledClass.Id, 8 + refund, 1, 2);
        await WithDbAsync(async db =>
        {
            var booking = await db.Enrollments.SingleAsync(x => x.Id == enrollment.Id);
            Assert.AreEqual("cancelled", booking.Status);
            Assert.AreEqual(Now, booking.CancelledAt);
            Assert.AreEqual(refund, booking.RefundedVisits);
            var entity = await db.Classes.IgnoreQueryFilters().SingleAsync(x => x.Id == scheduledClass.Id);
            entity.IsCancelled = !cancelled;
            entity.StartsAt = Now.AddDays(-1);
            await db.SaveChangesAsync();
        });
        using var repeat = await client.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, repeat.StatusCode);
        Assert.AreEqual(original, await repeat.Content.ReadAsStringAsync());
        await AssertStateAsync(user.Id, scheduledClass.Id, 8 + refund, 1, 2);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task ActiveBookingCannotBeCancelledAtOrAfterStart(int minutes)
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        await WithDbAsync(async db =>
        {
            var entity = await db.Classes.SingleAsync(x => x.Id == scheduledClass.Id);
            entity.StartsAt = Now.AddMinutes(minutes);
            await db.SaveChangesAsync();
        });
        using var client = Client(user);
        using var response = await client.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        await AssertReasonAsync(response, "class-started");
        await AssertStateAsync(user.Id, scheduledClass.Id, 8, 1, 2);
    }

    [TestMethod]
    public async Task ConcurrentCancellationsRefundExactlyOnce()
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        using var client = Client(user);
        var responses = await TogetherAsync(Enumerable.Range(0, 8).Select<int, Func<Task<HttpResponseMessage>>>(_ =>
            () => client.PostAsync($"/enrollments/{enrollment.Id}/cancel", null)));
        var bodies = new List<string>();
        foreach (var response in responses)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            bodies.Add(await response.Content.ReadAsStringAsync());
            response.Dispose();
        }
        Assert.AreEqual(1, bodies.Distinct().Count());
        await AssertStateAsync(user.Id, scheduledClass.Id, 10, 1, 2);
    }

    [TestMethod]
    public async Task ConcurrentChangesAndCancellationPreserveAccounting()
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        using var client = Client(user);
        var responses = await TogetherAsync(new Func<Task<HttpResponseMessage>>[]
        {
            () => client.PostAsJsonAsync($"/enrollments/{enrollment.Id}/seats", new { seats = 5 }),
            () => client.PostAsJsonAsync($"/enrollments/{enrollment.Id}/seats", new { seats = 1 }),
            () => client.PostAsync($"/enrollments/{enrollment.Id}/cancel", null)
        });
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.Conflict) await AssertReasonAsync(response, "enrollment-cancelled");
            else Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }
        await WithDbAsync(async db =>
        {
            Assert.AreEqual(10, (await db.Passes.SingleAsync(x => x.UserId == user.Id)).RemainingVisits);
            Assert.AreEqual("cancelled", (await db.Enrollments.SingleAsync(x => x.Id == enrollment.Id)).Status);
            Assert.AreEqual(0L, await new EnrollmentRepository(db.Enrollments).GetBookedSeatsAsync(scheduledClass.Id));
        });
    }

    [TestMethod]
    public async Task ConcurrentIncreasesOnDifferentClassesSharePassSafely()
    {
        var (user, firstClass, first) = await BookedScenarioAsync(visits: 3, seats: 1);
        var secondClass = await AddClassAsync();
        using var client = Client(user);
        using var secondCreate = await CreateAsync(client, secondClass.Id, 1);
        var second = await secondCreate.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        var responses = await TogetherAsync(new Func<Task<HttpResponseMessage>>[]
        {
            () => client.PostAsJsonAsync($"/enrollments/{first.Id}/seats", new { seats = 2 }),
            () => client.PostAsJsonAsync($"/enrollments/{second!.Id}/seats", new { seats = 2 })
        });
        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        await AssertReasonAsync(responses.Single(x => x.StatusCode != HttpStatusCode.OK), "pass-exhausted");
        foreach (var response in responses) response.Dispose();
        await WithDbAsync(async db =>
        {
            Assert.AreEqual(0, (await db.Passes.SingleAsync(x => x.UserId == user.Id)).RemainingVisits);
            Assert.AreEqual(3, await db.Enrollments.Where(x => x.UserId == user.Id).SumAsync(x => x.Seats));
        });
    }

    [TestMethod]
    public async Task AllLifecycleRoutesShareTheSameRateLimit()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        for (var i = 0; i < 60; i++)
        {
            using var response = await LifecycleAsync(client, "-1", i % 3 == 0 ? "card" : i % 3 == 1 ? "seats" : "cancel");
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var limited = await CreateAsync(client, -1, 1);
        Assert.AreEqual((HttpStatusCode)429, limited.StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentIncreasesCompeteForLastSeat()
    {
        var (firstUser, scheduledClass, first) = await BookedScenarioAsync(seats: 1);
        var secondUser = await AddUserAsync();
        using var firstClient = Client(firstUser);
        using var secondClient = Client(secondUser);
        using var secondCreated = await CreateAsync(secondClient, scheduledClass.Id, 1);
        var second = await secondCreated.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        await WithDbAsync(async db =>
        {
            var entity = await db.Classes.SingleAsync(x => x.Id == scheduledClass.Id);
            entity.Capacity = 3;
            await db.SaveChangesAsync();
        });
        var responses = await TogetherAsync(new Func<Task<HttpResponseMessage>>[]
        {
            () => firstClient.PostAsJsonAsync($"/enrollments/{first.Id}/seats", new { seats = 2 }),
            () => secondClient.PostAsJsonAsync($"/enrollments/{second!.Id}/seats", new { seats = 2 })
        });
        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        await AssertReasonAsync(responses.Single(x => x.StatusCode != HttpStatusCode.OK), "capacity-exceeded");
        foreach (var response in responses) response.Dispose();
        await WithDbAsync(async db =>
        {
            Assert.AreEqual(3L, await new EnrollmentRepository(db.Enrollments).GetBookedSeatsAsync(scheduledClass.Id));
            Assert.AreEqual(17, await db.Passes.Where(x => x.UserId == firstUser.Id || x.UserId == secondUser.Id)
                .SumAsync(x => x.RemainingVisits));
        });
    }

    [TestMethod]
    public async Task RealChangeAndCancellationDoNotChangeCreateReplay()
    {
        var (user, scheduledClass) = await ScenarioAsync();
        using var client = Client(user);
        var key = Guid.NewGuid().ToString();
        using var created = await CreateAsync(client, scheduledClass.Id, 2, key);
        var original = await created.Content.ReadAsStringAsync();
        var enrollment = await created.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        using var changed = await client.PostAsJsonAsync($"/enrollments/{enrollment!.Id}/seats", new { seats = 3 });
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        using var cancelled = await client.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        using var replay = await CreateAsync(client, scheduledClass.Id, 2, key);
        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
        Assert.AreEqual(original, await replay.Content.ReadAsStringAsync());
        await AssertStateAsync(user.Id, scheduledClass.Id, 10, 1, 3);
    }

    [TestMethod]
    public async Task HistoricalCancelledBookingReplaysWithoutChangingPass()
    {
        User user = null!;
        long id = 0;
        int refund = 0;
        int balance = 0;
        await WithDbAsync(async db =>
        {
            var historical = await db.Enrollments.Where(x => x.Status == "cancelled" && x.CreatedAt < Now.AddYears(-1))
                .AsNoTracking().FirstAsync();
            id = historical.Id;
            refund = historical.RefundedVisits;
            user = await db.Users.SingleAsync(x => x.Id == historical.UserId);
            balance = (await db.Passes.SingleAsync(x => x.UserId == user.Id)).RemainingVisits;
        });
        using var client = Client(user);
        using var first = await client.PostAsync($"/enrollments/{id}/cancel", null);
        using var second = await client.PostAsync($"/enrollments/{id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        var response = await first.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        Assert.AreEqual(refund, response!.RefundedVisits);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        await WithDbAsync(async db => Assert.AreEqual(balance,
            (await db.Passes.SingleAsync(x => x.UserId == user.Id)).RemainingVisits));
    }

    [DataTestMethod]
    [DataRow("change")]
    [DataRow("cancel")]
    public async Task LifecycleSaveFailureRollsBackAllChanges(string operation)
    {
        var (user, scheduledClass, enrollment) = await BookedScenarioAsync();
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(TestDatabase.PostgresConnectionString).AddInterceptors(new FailLifecycleSave()).Options;
        await using (var db = new ClubDbContext(options))
        {
            var service = new EnrollmentService(new UnitOfWork(db), new IdempotencyRepository(db), new FixedClock());
            if (operation == "change")
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.ChangeSeatsAsync(user.Id, enrollment.Id, 4));
            else
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.CancelAsync(user.Id, enrollment.Id));
        }
        await AssertStateAsync(user.Id, scheduledClass.Id, 8, 1, 2);
        await WithDbAsync(async db =>
        {
            var booking = await db.Enrollments.SingleAsync(x => x.Id == enrollment.Id);
            Assert.AreEqual("active", booking.Status);
            Assert.IsNull(booking.CancelledAt);
            Assert.AreEqual(0, booking.RefundedVisits);
        });
    }

    private static void AssertFields(JsonElement element, params string[] fields)
    {
        CollectionAssert.AreEquivalent(fields, element.EnumerateObject().Select(x => x.Name).ToArray());
    }

    private static Task<HttpResponseMessage> LifecycleAsync(HttpClient client, string id, string operation)
    {
        return operation switch
        {
            "card" => client.GetAsync($"/enrollments/{id}"),
            "seats" => client.PostAsJsonAsync($"/enrollments/{id}/seats", new { seats = 2 }),
            _ => client.PostAsync($"/enrollments/{id}/cancel", null)
        };
    }

    private static async Task<(User User, Class Class, CreateEnrollmentResponse Enrollment)> BookedScenarioAsync(
        int visits = 10, int seats = 2, int capacity = 10)
    {
        var (user, scheduledClass) = await ScenarioAsync(visits: visits, capacity: capacity);
        using var client = Client(user);
        using var response = await CreateAsync(client, scheduledClass.Id, seats);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (user, scheduledClass, (await response.Content.ReadFromJsonAsync<CreateEnrollmentResponse>())!);
    }

    private sealed class FailLifecycleSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated lifecycle storage failure");
        }
    }
}
