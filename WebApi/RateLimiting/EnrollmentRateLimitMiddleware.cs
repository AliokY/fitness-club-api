using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.RateLimiting;

public sealed class EnrollmentRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, EnrollmentRateLimiter limiter, IProblemDetailsService problems)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<EnrollmentRateLimitAttribute>() is not null
            && context.User.Identity?.IsAuthenticated == true)
        {
            var userId = long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            if (!await limiter.TryAcquireAsync(userId))
            {
                context.Response.StatusCode = 429;
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails = new ProblemDetails { Status = 429, Title = "Too many requests" }
                });
                return;
            }
        }
        await next(context);
    }
}
