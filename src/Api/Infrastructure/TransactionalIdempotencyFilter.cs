using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Domain;
using Api.Features.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Api.Infrastructure;

/// <summary>
/// Makes a write endpoint safe to call twice: the business change and the receipt that lets the
/// second call replay it commit together, or neither does.
/// </summary>
/// <remarks>
/// <para>
/// The previous filter recorded the receipt in a second <c>SaveChanges</c>, after the handler had
/// already committed. A crash between the two left an effect nothing could replay, and the next
/// retry did the work again. This one opens the transaction itself and invokes the handler inside
/// it — the handler's own <c>SaveChangesAsync</c> calls enlist automatically, because it is the same
/// scoped <see cref="AppDbContext"/> — so there is no window between the two facts to crash in.
/// </para>
/// <para>
/// <strong>No handler under this filter may call an external service.</strong> Holding a database
/// transaction across provider I/O would trade a correctness problem for lock contention and an
/// unbounded transaction. External work goes into an outbox row, which is local state and commits
/// with everything else.
/// </para>
/// <para>
/// The filter reads the result the handler returned rather than the bytes on the wire, because
/// minimal APIs execute the result <em>after</em> the filter pipeline unwinds — there is no response
/// body to capture from in here. Every endpoint under this filter returns an
/// <see cref="IValueHttpResult"/>, so the value and status are enough to replay it faithfully.
/// </para>
/// </remarks>
public sealed class TransactionalIdempotencyFilter(
    AppDbContext db,
    IClock clock,
    IFaultInjector faults,
    ILogger<TransactionalIdempotencyFilter> logger)
    : IEndpointFilter
{
    /// <summary>
    /// How long a duplicate waits for the request it is a duplicate of. Long enough for a short
    /// local handler, short enough that a stuck one answers rather than holding a connection.
    /// </summary>
    public const string LockTimeout = "3000ms";

    /// <summary>Bounds the body read, so the hash cannot be made expensive by the caller.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>Matches the column, and is generous for a UUID or a client-generated token.</summary>
    public const int MaxKeyLength = 100;

    private const string LockNotAvailable = "55P03";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var cancellationToken = http.RequestAborted;

        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var header)
            || header.ToString() is not { Length: > 0 } key)
        {
            // Required rather than opt-in from F5 on. A write with no key cannot be retried safely,
            // and "the caller forgot" is not a reason to let a payment happen twice.
            return Problem(
                "Idempotency-Key required",
                "Every write to this API must carry an Idempotency-Key header so a retry cannot repeat it.",
                StatusCodes.Status400BadRequest,
                "idempotency_key_required");
        }

        if (key.Length > MaxKeyLength)
        {
            return Problem(
                "Idempotency-Key too long",
                $"An Idempotency-Key may be at most {MaxKeyLength} characters.",
                StatusCodes.Status400BadRequest,
                "idempotency_key_invalid");
        }

        var userId = http.User.Id();
        var operation = $"{http.Request.Method} {http.Request.Path}";
        var requestHash = await HashRequestAsync(http, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Bounds every wait inside this transaction: the claim's conflict wait, the duplicate's row
        // lock, and anything the handler touches. A caller gets an answer rather than a hung socket.
        await db.Database.ExecuteSqlRawAsync($"SET LOCAL lock_timeout = '{LockTimeout}'", cancellationToken);

        int claimed;

        try
        {
            claimed = await ClaimAsync(key, userId, operation, requestHash, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState is LockNotAvailable)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return InProgress(key);
        }

        if (claimed == 0)
        {
            var replay = await ReplayAsync(key, userId, operation, requestHash, cancellationToken);
            await transaction.RollbackAsync(CancellationToken.None);
            return replay;
        }

        object? result;

        try
        {
            result = await next(context);
        }
        catch
        {
            // The claim and whatever the handler managed to change go back together. The key is
            // free again, which is right: nothing happened, so a retry is a first attempt.
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (result is not (IValueHttpResult { Value: { } value } and IStatusCodeHttpResult { StatusCode: { } status }))
        {
            // Nothing to store, so nothing to promise. Every endpoint under this filter returns a
            // value result; a build that adds one that does not should not silently lose its receipt.
            logger.LogError("An endpoint under the idempotency filter returned {Result}, which cannot be replayed.", result?.GetType().Name);
            await transaction.RollbackAsync(CancellationToken.None);
            return result;
        }

        if (status >= StatusCodes.Status500InternalServerError)
        {
            // A server failure is not a settled outcome. Rolling back leaves the key retryable
            // instead of caching a 500 for thirty days.
            await transaction.RollbackAsync(CancellationToken.None);
            return result;
        }

        await CompleteAsync(key, userId, status, JsonSerializer.Serialize(value, value.GetType(), Json), cancellationToken);

        faults.Reach(FaultCheckpoint.BeforeBusinessTransactionCommit);
        await transaction.CommitAsync(cancellationToken);
        faults.Reach(FaultCheckpoint.AfterBusinessTransactionCommitBeforeResponse);

        return result;
    }

    /// <summary>
    /// Takes the key, or discovers somebody else has it. <c>ON CONFLICT DO NOTHING</c> rather than a
    /// read-then-insert: the database decides, so two simultaneous first attempts cannot both be
    /// first.
    /// </summary>
    private Task<int> ClaimAsync(
        string key,
        Guid userId,
        string operation,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        return db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO idempotency_keys
                ("Id", "Key", "UserId", "Operation", "RequestHash", "StatusCode", "ResponseJson", "CreatedAt", "ExpiresAt")
            VALUES
                ({Guid.CreateVersion7()}, {key}, {userId}, {operation}, {requestHash}, 0, NULL, {now},
                 {now + IdempotencyRecord.Retention})
            ON CONFLICT ("UserId", "Key") DO NOTHING
            """,
            cancellationToken);
    }

    /// <summary>
    /// Answers a caller whose key is already taken: the stored result if it is the same request,
    /// a refusal if it is not.
    /// </summary>
    private async Task<object> ReplayAsync(
        string key,
        Guid userId,
        string operation,
        string requestHash,
        CancellationToken cancellationToken)
    {
        try
        {
            // Waits out the twin still holding this key. Once its transaction ends, the row is
            // either committed and settled, or gone because it rolled back.
            await db.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM idempotency_keys WHERE "UserId" = {userId} AND "Key" = {key} FOR UPDATE""",
                cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState is LockNotAvailable)
        {
            return InProgress(key);
        }

        var existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(record => record.UserId == userId && record.Key == key, cancellationToken);

        if (existing is null)
        {
            // The twin rolled back and released the key. Nothing happened under it, so this caller
            // is not a duplicate — but it has lost its transaction, so it retries as a fresh request.
            return InProgress(key);
        }

        if (!SameRequest(existing, operation, requestHash))
        {
            // Same key, different content: the caller has a bug, and replaying the other request's
            // response would hide it behind a plausible-looking success.
            logger.LogWarning("Idempotency key {Key} was reused with different content.", key);

            return Problem(
                "Idempotency key reused",
                $"Key '{key}' was already used for a different request. Use a new key, or resend the original request unchanged.",
                StatusCodes.Status422UnprocessableEntity,
                "idempotency_key_payload_mismatch");
        }

        if (!existing.IsCompleted)
        {
            return InProgress(key);
        }

        using var document = JsonDocument.Parse(existing.ResponseJson ?? "null");
        return Results.Json(document.RootElement.Clone(), statusCode: existing.StatusCode);
    }

    /// <summary>
    /// Whether the stored claim is the same request. Rows written before request hashing fall back
    /// to the old operation check and stay replay-only until they expire — see the rollout note in
    /// ADR 009.
    /// </summary>
    private static bool SameRequest(IdempotencyRecord existing, string operation, string requestHash) =>
        existing.RequestHash is { Length: > 0 } stored
            ? string.Equals(stored, requestHash, StringComparison.Ordinal)
            : string.Equals(existing.Operation, operation, StringComparison.Ordinal);

    private Task CompleteAsync(
        string key,
        Guid userId,
        int status,
        string responseJson,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"""
            UPDATE idempotency_keys
            SET "StatusCode" = {status}, "ResponseJson" = {responseJson}::json, "CompletedAt" = {clock.UtcNow}
            WHERE "UserId" = {userId} AND "Key" = {key}
            """,
            cancellationToken);

    /// <summary>
    /// The fingerprint the replay decision is made on. Credentials are not part of it and are never
    /// stored: what identifies the request is who sent it plus what they asked for, and the scope
    /// already carries the who.
    /// </summary>
    private static async Task<string> HashRequestAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(http.Request, cancellationToken);

        var payload = new StringBuilder()
            .Append(http.Request.Method).Append('\n')
            .Append(http.Request.Path.Value).Append(http.Request.QueryString.Value).Append('\n')
            .Append(Convert.ToHexStringLower(SHA256.HashData(body))).Append('\n')
            .Append(http.Request.Headers.IfMatch.ToString())
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Reads the body the model binder has already consumed.
    /// </summary>
    /// <remarks>
    /// An endpoint filter runs <em>after</em> parameter binding, so by the time this executes the
    /// request stream is at its end. <see cref="IdempotencyBodyBuffering"/> is what makes rewinding
    /// possible; without it every POST would hash to the same fingerprint and the mismatch check
    /// would silently degrade into a check on the URL alone. That is worth failing loudly over
    /// rather than approximating.
    /// </remarks>
    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0)
        {
            return [];
        }

        if (!request.Body.CanSeek)
        {
            throw new InvalidOperationException(
                "The request body cannot be rewound, so its fingerprint cannot be computed. "
                + $"Call {nameof(IdempotencyBodyBuffering)}.{nameof(IdempotencyBodyBuffering.UseIdempotencyBodyBuffering)} before routing.");
        }

        request.Body.Position = 0;

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        request.Body.Position = 0;

        // Bounded so a large body cannot make the hash the expensive part of the request. The model
        // binder refuses anything near this size anyway; the cap is here so the filter does not read
        // it into memory first.
        return buffer.Length > MaxBodyBytes ? buffer.GetBuffer()[..MaxBodyBytes] : buffer.ToArray();
    }

    private static IResult InProgress(string key) => Problem(
        "Request already in progress",
        $"Another request with key '{key}' is still running. Retry in a moment; it will replay that request's answer.",
        StatusCodes.Status409Conflict,
        "request_in_progress");

    private static IResult Problem(string title, string detail, int statusCode, string code) => Results.Problem(
        title: title,
        detail: detail,
        statusCode: statusCode,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>
/// Lets <see cref="TransactionalIdempotencyFilter"/> read a body that model binding has already
/// consumed.
/// </summary>
/// <remarks>
/// Scoped to requests that actually carry an <c>Idempotency-Key</c>, so the cost of buffering is
/// paid by the handful of writes that need a fingerprint rather than by every request the app
/// serves.
/// </remarks>
public static class IdempotencyBodyBuffering
{
    public static IApplicationBuilder UseIdempotencyBodyBuffering(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey("Idempotency-Key"))
            {
                context.Request.EnableBuffering();
            }

            return next();
        });
}
