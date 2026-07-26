using System.Security.Claims;
using System.Text.Json;
using Api.Domain;
using Api.Features.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure;

/// <summary>
/// Makes a write endpoint safe to call twice with the same <c>Idempotency-Key</c>: the second call
/// returns the first call's answer without touching the database.
/// </summary>
/// <remarks>
/// <para>
/// The filter reads the result the handler returned rather than the bytes on the wire, because
/// minimal APIs execute the result <em>after</em> the filter pipeline unwinds — there is no response
/// body to capture from in here. Every endpoint under this filter returns an
/// <see cref="IValueHttpResult"/>, so the value and status are enough to replay it faithfully.
/// </para>
/// <para>
/// Requests without the header pass straight through. The header is what opts in.
/// </para>
/// </remarks>
public sealed class IdempotencyFilter(AppDbContext db, IClock clock, ILogger<IdempotencyFilter> logger)
    : IEndpointFilter
{
    /// <summary>
    /// How long a key is remembered. Long enough to cover any retry that means the same thing,
    /// short enough that the table stays a cache rather than a second ledger.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var header)
            || header.ToString() is not { Length: > 0 } key)
        {
            return await next(context);
        }

        var userId = http.User.Id();
        var operation = $"{http.Request.Method} {http.Request.Path}";
        var cutoff = clock.UtcNow - Retention;

        var existing = await db.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Key == key && record.UserId == userId && record.CreatedAt > cutoff,
                http.RequestAborted);

        if (existing is not null)
        {
            return Replay(existing, operation, key);
        }

        var result = await next(context);

        if (result is IValueHttpResult { Value: { } value } and IStatusCodeHttpResult { StatusCode: { } status })
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = key,
                UserId = userId,
                Operation = operation,
                StatusCode = status,
                ResponseJson = JsonSerializer.Serialize(value, value.GetType(), Json),
                CreatedAt = clock.UtcNow,
            });

            try
            {
                await db.SaveChangesAsync(http.RequestAborted);
            }
            catch (DbUpdateException)
            {
                // Two requests raced through the lookup. The write already happened once; the unique
                // index is what stopped it being recorded twice, and the caller still gets an answer.
                logger.LogInformation("Idempotency key {Key} was recorded concurrently.", key);
            }
        }

        return result;
    }

    private static object Replay(IdempotencyRecord record, string operation, string key)
    {
        if (!string.Equals(record.Operation, operation, StringComparison.Ordinal))
        {
            // Same key, different operation: the caller has a bug, and silently replaying the other
            // operation's response would hide it behind a plausible-looking success.
            return Results.Problem(
                title: "Idempotency key reused",
                detail: $"Key '{key}' was already used for {record.Operation}.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "idempotency_key_reused" });
        }

        using var document = JsonDocument.Parse(record.ResponseJson);
        return Results.Json(document.RootElement.Clone(), statusCode: record.StatusCode);
    }
}
