using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Analytics;
using WebApi.RateLimiting;

namespace WebApi.Controllers;

[ApiController]
[Authorize(Roles = AdminRole)]
[Route("analytics")]
[EnrollmentRateLimit]
public sealed class AnalyticsController(AnalyticsService service) : BaseApiController
{
    [HttpGet("load")]
    [ProducesResponseType<AnalyticsLoadResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLoad(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] long? roomId,
        CancellationToken ct)
    {
        return Ok(await service.GetLoadAsync(from, to, roomId, ct));
    }
}
