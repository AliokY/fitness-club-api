using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Enrollments;
using WebApi.RateLimiting;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[Route("enrollments")]
[EnrollmentRateLimit]
public sealed class EnrollmentsController(EnrollmentService service) : BaseApiController
{
    [HttpPost("waitlist")]
    [ProducesResponseType<JoinWaitlistResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> JoinWaitlist(
        [FromBody] JoinWaitlistRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CancellationToken ct)
    {
        var keys = Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(idempotencyKey))
            return Problem(statusCode: 400, title: "Idempotency-Key is required and must have a single nonblank value");

        var response = await service.JoinWaitlistAsync(CurrentUserId, request.ClassId!.Value,
            request.Seats!.Value, idempotencyKey, ct);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("waitlist/{id}/cancel")]
    [ProducesResponseType<CancelWaitlistResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelWaitlist(long id, CancellationToken ct)
    {
        return Ok(await service.CancelWaitlistAsync(CurrentUserId, id, ct));
    }

    [HttpGet("{id}")]
    [ProducesResponseType<EnrollmentCardResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCard(long id, CancellationToken ct)
    {
        return Ok(await service.GetCardAsync(CurrentUserId, id, ct));
    }

    [HttpPost("{id}/seats")]
    [ProducesResponseType<EnrollmentCardResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangeSeats(long id, [FromBody] ChangeSeatsRequest request, CancellationToken ct)
    {
        return Ok(await service.ChangeSeatsAsync(CurrentUserId, id, request.Seats!.Value, ct));
    }

    [HttpPost("{id}/cancel")]
    [ProducesResponseType<CancelEnrollmentResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        return Ok(await service.CancelAsync(CurrentUserId, id, ct));
    }

    [HttpPost]
    [ProducesResponseType<CreateEnrollmentResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateEnrollmentRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        CancellationToken ct)
    {
        var keys = Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(idempotencyKey))
            return Problem(statusCode: 400, title: "Idempotency-Key is required and must have a single nonblank value");

        var response = await service.CreateAsync(CurrentUserId, request.ClassId!.Value, request.Seats!.Value, idempotencyKey, ct);
        return StatusCode(StatusCodes.Status201Created, response);
    }
}
