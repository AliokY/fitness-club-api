using System.Net;
using System.Net.Http.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Analytics;

namespace WebApi.Tests;

public partial class EnrollmentCreationTests
{
    [TestMethod]
    public async Task AnalyticsGroupsUtcDayAndRoomAndCountsOnlyActiveClassesAndEnrollments()
    {
        var admin = await MakeAdminAsync();
        var attendee = await AddUserAsync();
        using var client = Client(admin);
        using var attendeeClient = Client(attendee);
        var baseDate = new DateTime(2040, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var first = await AddClassAsync(capacity: 10, startsAt: baseDate.AddHours(23));
        var second = await AddClassAsync(capacity: 20, startsAt: baseDate.AddHours(22));
        var nextDay = await AddClassAsync(capacity: 30, startsAt: baseDate.AddDays(1).AddMinutes(15));
        var cancelledClass = await AddClassAsync(capacity: 999, startsAt: baseDate.AddHours(21), cancelled: true);
        var otherRoomId = 0L;
        await WithDbAsync(async db =>
        {
            otherRoomId = await db.Rooms.Where(x => x.Id != first.RoomId).Select(x => x.Id).FirstAsync();
            (await db.Classes.SingleAsync(x => x.Id == nextDay.Id)).RoomId = otherRoomId;
            await db.SaveChangesAsync();
        });

        using var firstBooking = await CreateAsync(attendeeClient, first.Id, 2);
        using var cancelledBooking = await CreateAsync(attendeeClient, second.Id, 4);
        using var nextDayBooking = await CreateAsync(attendeeClient, nextDay.Id, 1);
        await WithDbAsync(async db =>
        {
            (await db.Enrollments.SingleAsync(x => x.ClassId == second.Id && x.Status == "active")).Status = "cancelled";
            await db.SaveChangesAsync();
        });

        using var response = await client.GetAsync("/analytics/load?from=2040-04-15&to=2040-04-16");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<AnalyticsLoadResponse>();
        Assert.AreEqual(2, result!.Items.Count);
        var firstDay = result.Items[0];
        Assert.AreEqual(new DateOnly(2040, 4, 15), firstDay.Date);
        Assert.AreEqual(first.RoomId, firstDay.Room.Id);
        Assert.AreEqual(2, firstDay.ClassesCount);
        Assert.AreEqual(30, firstDay.TotalCapacity);
        Assert.AreEqual(2, firstDay.BookedSeats);
        var secondDay = result.Items[1];
        Assert.AreEqual(new DateOnly(2040, 4, 16), secondDay.Date);
        Assert.AreEqual(otherRoomId, secondDay.Room.Id);
        Assert.AreEqual(1, secondDay.ClassesCount);
        Assert.AreEqual(30, secondDay.TotalCapacity);
        Assert.AreEqual(1, secondDay.BookedSeats);
    }

    [TestMethod]
    public async Task AnalyticsFiltersByRoomAndAllowsSingleInclusiveDate()
    {
        var admin = await MakeAdminAsync();
        using var client = Client(admin);
        var date = new DateTime(2041, 8, 9, 12, 0, 0, DateTimeKind.Utc);
        var selected = await AddClassAsync(capacity: 7, startsAt: date);
        var other = await AddClassAsync(capacity: 11, startsAt: date.AddHours(1));
        var otherRoomId = await RoomDifferentFromAsync(selected.RoomId);
        await WithDbAsync(async db =>
        {
            (await db.Classes.SingleAsync(x => x.Id == other.Id)).RoomId = otherRoomId;
            await db.SaveChangesAsync();
        });

        using var response = await client.GetAsync($"/analytics/load?from=2041-08-09&to=2041-08-09&roomId={selected.RoomId}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<AnalyticsLoadResponse>();
        var item = result!.Items.Single();
        Assert.AreEqual(new DateOnly(2041, 8, 9), item.Date);
        Assert.AreEqual(selected.RoomId, item.Room.Id);
        Assert.AreEqual(1, item.ClassesCount);
        Assert.AreEqual(7, item.TotalCapacity);
        Assert.AreEqual(0, item.BookedSeats);
    }

    [TestMethod]
    public async Task AnalyticsReturnsEmptyForUnknownRoomAndRequiresAdminRole()
    {
        var admin = await MakeAdminAsync();
        var user = await AddUserAsync();
        using var adminClient = Client(admin);
        using var userClient = Client(user);
        using var noRoom = await adminClient.GetAsync("/analytics/load?from=2042-01-01&to=2042-01-01&roomId=9223372036854775807");
        Assert.AreEqual(HttpStatusCode.OK, noRoom.StatusCode);
        Assert.AreEqual(0, (await noRoom.Content.ReadFromJsonAsync<AnalyticsLoadResponse>())!.Items.Count);
        using var forbidden = await userClient.GetAsync("/analytics/load?from=2042-01-01&to=2042-01-01");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var anonymous = _host.CreateClient();
        using var unauthorized = await anonymous.GetAsync("/analytics/load?from=2042-01-01&to=2042-01-01");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [TestMethod]
    public async Task AnalyticsValidatesRequiredDatesRangeAndFormats()
    {
        var admin = await MakeAdminAsync();
        using var client = Client(admin);
        using var missing = await client.GetAsync("/analytics/load");
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        using var missingTo = await client.GetAsync("/analytics/load?from=2040-01-01");
        Assert.AreEqual(HttpStatusCode.BadRequest, missingTo.StatusCode);
        using var malformedDate = await client.GetAsync("/analytics/load?from=2040-1-1&to=2040-01-01");
        Assert.AreEqual(HttpStatusCode.BadRequest, malformedDate.StatusCode);
        using var malformedRoom = await client.GetAsync("/analytics/load?from=2040-01-01&to=2040-01-01&roomId=nope");
        Assert.AreEqual(HttpStatusCode.BadRequest, malformedRoom.StatusCode);
        using var reversedRange = await client.GetAsync("/analytics/load?from=2040-01-02&to=2040-01-01");
        Assert.AreEqual(HttpStatusCode.BadRequest, reversedRange.StatusCode);
    }

    [TestMethod]
    public async Task AnalyticsUsesSharedSixtyRequestsPerMinuteRateLimit()
    {
        var admin = await MakeAdminAsync();
        using var client = Client(admin);
        for (var i = 0; i < 60; i++)
        {
            using var response = await client.GetAsync("/analytics/load?from=2043-01-01&to=2043-01-01");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        using var limited = await client.GetAsync("/analytics/load?from=2043-01-01&to=2043-01-01");
        Assert.AreEqual((HttpStatusCode)429, limited.StatusCode);
    }

    [TestMethod]
    public async Task ForbiddenAnalyticsRequestsCountTowardSharedRateLimitAcrossRoutes()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        for (var i = 0; i < 60; i++)
        {
            using var response = i % 2 == 0
                ? await client.GetAsync("/analytics/load?from=2044-01-01&to=2044-01-01")
                : await GetScheduleAsync(client, Now, Now.AddDays(1));
            var expected = i % 2 == 0 ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            Assert.AreEqual(expected, response.StatusCode, $"Request {i + 1}: {await response.Content.ReadAsStringAsync()}");
        }

        using var sixtyFirst = await client.GetAsync("/analytics/load?from=2044-01-01&to=2044-01-01");
        Assert.AreEqual((HttpStatusCode)429, sixtyFirst.StatusCode);
    }

    private static async Task<User> MakeAdminAsync()
    {
        var user = await AddUserAsync();
        user.Role = "admin";
        await WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(x => x.Id == user.Id)).Role = "admin";
            await db.SaveChangesAsync();
        });
        return user;
    }

    private static async Task<long> RoomDifferentFromAsync(long roomId)
    {
        var result = 0L;
        await WithDbAsync(async db => result = await db.Rooms.Where(x => x.Id != roomId).Select(x => x.Id).FirstAsync());
        return result;
    }
}
