using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Schedule;

namespace WebApi.Tests;

public partial class EnrollmentCreationTests
{
    [TestMethod]
    public async Task ScheduleReturnsContractFieldsAndActiveSeatTotalsForCurrentUser()
    {
        var (user, scheduledClass) = await ScenarioAsync(capacity: 4, startsAt: Now.AddYears(4));
        var otherUser = await AddUserAsync();
        using var client = Client(user);
        using var otherClient = Client(otherUser);
        using var first = await CreateAsync(client, scheduledClass.Id, 1);
        using var second = await CreateAsync(client, scheduledClass.Id, 1);
        using var others = await CreateAsync(otherClient, scheduledClass.Id, 1);

        using var response = await GetScheduleAsync(client, scheduledClass.StartsAt, scheduledClass.StartsAt.AddSeconds(1));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<ScheduleResponse>();
        var item = result!.Items.Single(x => x.ClassId == scheduledClass.Id);
        Assert.AreEqual(scheduledClass.Title, item.Title);
        Assert.AreEqual(scheduledClass.StartsAt, item.StartsAt);
        Assert.AreEqual(scheduledClass.DurationMinutes, item.DurationMinutes);
        Assert.AreEqual(scheduledClass.RoomId, item.Room.Id);
        Assert.IsFalse(string.IsNullOrWhiteSpace(item.Room.Name));
        Assert.AreEqual(4, item.Capacity);
        Assert.AreEqual(1, item.FreeSeats);
        Assert.AreEqual(2, item.MySeats);
        Assert.IsNull(result.ContinuationToken);
    }

    [TestMethod]
    public async Task ScheduleUsesInclusiveStartExclusiveEndAndHidesCancelledClasses()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        var atFrom = await AddClassAsync(startsAt: Now.AddHours(1));
        var beforeTo = await AddClassAsync(startsAt: Now.AddHours(2));
        var atTo = await AddClassAsync(startsAt: Now.AddHours(3));
        var cancelled = await AddClassAsync(startsAt: Now.AddHours(1.5), cancelled: true);

        using var response = await GetScheduleAsync(client, atFrom.StartsAt, atTo.StartsAt);
        var result = await response.Content.ReadFromJsonAsync<ScheduleResponse>();
        var ids = result!.Items.Select(x => x.ClassId).ToArray();
        CollectionAssert.Contains(ids, atFrom.Id);
        CollectionAssert.Contains(ids, beforeTo.Id);
        CollectionAssert.DoesNotContain(ids, atTo.Id);
        CollectionAssert.DoesNotContain(ids, cancelled.Id);

        using var noOffset = await client.GetAsync("/schedule?from=2030-04-15T13%3A00%3A00&to=2030-04-15T13%3A00%3A01");
        var noOffsetResult = await noOffset.Content.ReadFromJsonAsync<ScheduleResponse>();
        CollectionAssert.Contains(noOffsetResult!.Items.Select(x => x.ClassId).ToArray(), atFrom.Id);
    }

    [TestMethod]
    public async Task ScheduleUsesStableKeysetPaginationAndAllowsChangingPageSize()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        var classes = new[]
        {
            await AddClassAsync(startsAt: Now.AddHours(1)),
            await AddClassAsync(startsAt: Now.AddHours(1)),
            await AddClassAsync(startsAt: Now.AddHours(1)),
            await AddClassAsync(startsAt: Now.AddHours(2))
        };

        var collected = new List<long>();
        string? token = null;
        do
        {
            using var response = await GetScheduleAsync(client, Now, Now.AddDays(1), limit: token is null ? 2 : 1, token: token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var page = await response.Content.ReadFromJsonAsync<ScheduleResponse>();
            collected.AddRange(page!.Items.Where(x => classes.Any(c => c.Id == x.ClassId)).Select(x => x.ClassId));
            token = page.ContinuationToken;
        } while (token is not null);

        CollectionAssert.AreEqual(classes.OrderBy(x => x.StartsAt).ThenBy(x => x.Id).Select(x => x.Id).ToArray(),
            collected.ToArray());
    }

    [TestMethod]
    public async Task ScheduleBindsContinuationTokenToFiltersAndRejectsMalformedToken()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        for (var i = 0; i < 2; i++)
            await AddClassAsync(startsAt: Now.AddHours(i + 1));

        using var first = await GetScheduleAsync(client, Now, Now.AddDays(1), limit: 1);
        var firstPage = await first.Content.ReadFromJsonAsync<ScheduleResponse>();
        Assert.IsNotNull(firstPage!.ContinuationToken);
        using var changedLimit = await GetScheduleAsync(client, Now, Now.AddDays(1), limit: 2,
            token: firstPage.ContinuationToken);
        Assert.AreEqual(HttpStatusCode.OK, changedLimit.StatusCode);
        using var changedFilter = await GetScheduleAsync(client, Now, Now.AddDays(2), limit: 1,
            token: firstPage.ContinuationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, changedFilter.StatusCode);
        using var malformed = await GetScheduleAsync(client, Now, Now.AddDays(1), token: "not-a-token!");
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [TestMethod]
    public async Task ScheduleValidatesRequiredDatesRangeLimitAndQueryFormats()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        using var missing = await client.GetAsync("/schedule");
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        using var missingTo = await client.GetAsync($"/schedule?from={Uri.EscapeDataString(Now.ToString("O"))}");
        Assert.AreEqual(HttpStatusCode.BadRequest, missingTo.StatusCode);
        using var invalidDate = await client.GetAsync("/schedule?from=nope&to=nope");
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidDate.StatusCode);
        using var invalidRoom = await client.GetAsync("/schedule?from=2030-04-15T12%3A00%3A00Z&to=2030-04-16T12%3A00%3A00Z&roomId=x");
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidRoom.StatusCode);
        using var invalidLimit = await GetScheduleAsync(client, Now, Now.AddDays(1), limit: 0);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidLimit.StatusCode);
        using var oversizedLimit = await GetScheduleAsync(client, Now, Now.AddDays(1), limit: 101);
        Assert.AreEqual(HttpStatusCode.BadRequest, oversizedLimit.StatusCode);
        using var invalidRange = await GetScheduleAsync(client, Now, Now);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidRange.StatusCode);
    }

    [TestMethod]
    public async Task ScheduleRequiresAuthenticationAndMissingRoomReturnsEmptyPage()
    {
        using var anonymous = _host.CreateClient();
        using var unauthorized = await GetScheduleAsync(anonymous, Now, Now.AddDays(1));
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var user = await AddUserAsync();
        using var client = Client(user);
        using var missingRoom = await GetScheduleAsync(client, Now, Now.AddDays(1), roomId: long.MaxValue);
        Assert.AreEqual(HttpStatusCode.OK, missingRoom.StatusCode);
        var result = await missingRoom.Content.ReadFromJsonAsync<ScheduleResponse>();
        Assert.AreEqual(0, result!.Items.Count);
        Assert.IsNull(result.ContinuationToken);
    }

    [TestMethod]
    public async Task ScheduleFiltersByRoom()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        var first = await AddClassAsync(startsAt: Now.AddHours(1));
        var second = await AddClassAsync(startsAt: Now.AddHours(2));
        var otherRoom = 0L;
        await WithDbAsync(async db =>
        {
            otherRoom = await db.Rooms.Where(x => x.Id != first.RoomId).Select(x => x.Id).FirstAsync();
            (await db.Classes.SingleAsync(x => x.Id == second.Id)).RoomId = otherRoom;
            await db.SaveChangesAsync();
        });

        using var response = await GetScheduleAsync(client, Now, Now.AddDays(1), roomId: first.RoomId);
        var result = await response.Content.ReadFromJsonAsync<ScheduleResponse>();
        var ids = result!.Items.Select(x => x.ClassId).ToArray();
        CollectionAssert.Contains(ids, first.Id);
        CollectionAssert.DoesNotContain(ids, second.Id);
    }

    [TestMethod]
    public async Task ScheduleUsesSharedSixtyRequestsPerMinuteRateLimit()
    {
        var user = await AddUserAsync();
        using var client = Client(user);
        for (var i = 0; i < 60; i++)
        {
            using var response = await GetScheduleAsync(client, Now, Now.AddDays(1));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        using var limited = await GetScheduleAsync(client, Now, Now.AddDays(1));
        Assert.AreEqual((HttpStatusCode)429, limited.StatusCode);
    }

    private static Task<HttpResponseMessage> GetScheduleAsync(HttpClient client, DateTimeOffset from,
        DateTimeOffset to, int? limit = null, long? roomId = null, string? token = null)
    {
        var query = $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        if (roomId.HasValue) query += $"&roomId={roomId.Value}";
        if (limit.HasValue) query += $"&limit={limit.Value}";
        if (token is not null) query += $"&continuationToken={Uri.EscapeDataString(token)}";
        return client.GetAsync($"/schedule?{query}");
    }

    private static Task<HttpResponseMessage> GetScheduleAsync(HttpClient client, DateTime from,
        DateTime to, int? limit = null, long? roomId = null, string? token = null) =>
        GetScheduleAsync(client, new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)), limit, roomId, token);
}
