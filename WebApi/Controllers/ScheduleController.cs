using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.RateLimiting;
using WebApi.Schedule;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[Route("schedule")]
[EnrollmentRateLimit]
public sealed class ScheduleController(ScheduleService service) : BaseApiController
{
    [HttpGet]
    [ProducesResponseType<ScheduleResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] long? roomId,
        [FromQuery] int? limit,
        [FromQuery] string? continuationToken,
        CancellationToken ct)
    {
        return Ok(await service.GetAsync(CurrentUserId, from, to, roomId, limit, continuationToken, ct));
    }
}
