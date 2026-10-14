using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ProductOrderApi.Errors;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request {TraceIdentifier} was aborted by the client.", httpContext.TraceIdentifier);
            return true;
        }

        ProblemDetails details;
        if (exception is AppException appException)
        {
            details = appException.Errors is null
                ? new ProblemDetails()
                : new ValidationProblemDetails(appException.Errors.ToDictionary(pair => pair.Key, pair => pair.Value));
            details.Status = appException.StatusCode;
            details.Title = appException.Title;
            details.Detail = appException.Detail;
        }
        else
        {
            logger.LogError(exception, "Unhandled exception while processing request {TraceIdentifier}.", httpContext.TraceIdentifier);
            details = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred",
                Detail = "The server encountered an unexpected error."
            };
        }

        details.Instance = httpContext.Request.Path;
        details.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        httpContext.Response.StatusCode = details.Status ?? StatusCodes.Status500InternalServerError;

        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details
        });

        return true;
    }
}
