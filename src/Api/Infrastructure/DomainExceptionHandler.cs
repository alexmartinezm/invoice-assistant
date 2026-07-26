using Api.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Api.Infrastructure;

/// <summary>
/// Turns a <see cref="DomainException"/> into a 409 carrying the machine-readable code. Tool
/// results are fed back to the model verbatim, so the error has to be self-explanatory: the
/// assistant is expected to relay it, and there is an eval asserting it does not invent success.
/// </summary>
public sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DomainException domainException)
        {
            return false;
        }

        logger.LogInformation(
            "Domain rule rejected {Method} {Path}: {Code}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            domainException.Code);

        var problem = new ProblemDetails
        {
            Title = "Operation rejected by the domain",
            Detail = domainException.Message,
            Status = StatusCodes.Status409Conflict,
            Extensions = { ["code"] = domainException.Code },
        };

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
