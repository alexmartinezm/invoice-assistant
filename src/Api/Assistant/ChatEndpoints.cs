using System.Net.ServerSentEvents;
using System.Security.Claims;
using Api.Assistant.Tools;
using Microsoft.Extensions.Options;

namespace Api.Assistant;

public static class ChatEndpoints
{
    /// <summary>Rate-limiting policy name for the chat endpoint.</summary>
    public const string RateLimitPolicy = "chat";

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/chat", PostAsync)
            .WithTags("Assistant")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy);

        // The catalog is the capability boundary, so it is worth being able to look at it.
        routes.MapGet("/api/assistant/tools", GetTools)
            .WithTags("Assistant")
            .RequireAuthorization();

        return routes;
    }

    private static IResult PostAsync(
        ChatRequest request,
        ClaimsPrincipal principal,
        ChatOrchestrator orchestrator,
        AssistantAvailability availability,
        IOptions<AssistantOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!availability.IsConfigured)
        {
            return Results.Problem(
                title: "Assistant not configured",
                detail: availability.Reason,
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["code"] = "assistant_not_configured" });
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.Problem(
                title: "Empty message",
                detail: "The message cannot be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var maxLength = options.Value.MaxInputCharacters;
        if (request.Message.Length > maxLength)
        {
            return Results.Problem(
                title: "Message too long",
                detail: $"The message must be at most {maxLength} characters; this one has {request.Message.Length}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var logger = loggerFactory.CreateLogger(typeof(ChatEndpoints));
        return TypedResults.ServerSentEvents(
            StreamAsync(request, principal, orchestrator, logger, cancellationToken));
    }

    /// <summary>
    /// Once the response has started there is no status code left to fail with, so a failure has
    /// to reach the client as an <c>error</c> event rather than as an HTTP error.
    /// </summary>
    private static async IAsyncEnumerable<SseItem<ChatEvent>> StreamAsync(
        ChatRequest request,
        ClaimsPrincipal principal,
        ChatOrchestrator orchestrator,
        ILogger logger,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var turn = orchestrator.RunTurnAsync(request, principal, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatEvent? next = null;
            ChatEvent? failure = null;

            try
            {
                if (await turn.MoveNextAsync())
                {
                    next = turn.Current;
                }
            }
            catch (OperationCanceledException)
            {
                // The user navigated away or closed the drawer; nothing to report.
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Chat turn failed");
                failure = new TurnFailed(
                    "The assistant could not complete the turn. Check the server logs for the trace id.");
            }

            if (failure is not null)
            {
                yield return ToSse(failure);
                yield break;
            }

            if (next is null)
            {
                yield break;
            }

            yield return ToSse(next);
        }
    }

    private static SseItem<ChatEvent> ToSse(ChatEvent chatEvent) => new(chatEvent, chatEvent.EventName());

    private static IResult GetTools(ToolCatalog catalog) =>
        Results.Ok(catalog.Tools.Select(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            sideEffect = tool.SideEffect.ToString().ToLowerInvariant(),
            requiredRole = tool.RequiredRole.ToString(),
            riskLevel = tool.RiskLevel.ToString().ToLowerInvariant(),
        }));
}
