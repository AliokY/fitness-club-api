using Microsoft.AspNetCore.Mvc;
using WebApi.Enrollments;

namespace WebApi.Middleware;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (EnrollmentException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
            var details = new ProblemDetails { Status = exception.StatusCode, Title = exception.Message };
            if (exception.Reason is not null) details.Extensions["reason"] = exception.Reason;
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context, ProblemDetails = details
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Unhandled exception while processing {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Internal server error"
                }
            });
        }
    }
}
