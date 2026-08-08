using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Api.Assistant.Tools;

/// <summary>
/// One call to our own API, with the status code kept rather than folded into the payload.
/// </summary>
/// <remarks>
/// The model is handed <see cref="Payload"/> and nothing else — a status code is not something to
/// ask it to reason about. An <c>ActionExecution</c>, on the other hand, has to classify: 2xx is a
/// settled effect, a deterministic 4xx proves the effect did not happen, and a 5xx cannot prove
/// either way. That distinction needs the number.
/// </remarks>
public sealed record ApiResponse(int StatusCode, JsonElement Payload, string? Code)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// The assistant's window onto the business API. Tools go over HTTP to our own REST API carrying
/// the caller's bearer token (ADR 002), so the assistant is just another API client: endpoint
/// authorization applies to it exactly as it applies to the browser, and the propagated identity
/// is visible in the traces.
/// </summary>
public sealed class SelfApiClient(HttpClient http, IHttpContextAccessor httpContextAccessor, ILogger<SelfApiClient> logger)
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns the API's response as parsed JSON on success, or a small structured error the model
    /// can act on — including a 403, which is what propagated identity looks like from here.
    /// </summary>
    /// <remarks>
    /// A <see cref="JsonElement"/> rather than a string on purpose: a tool that returns a string
    /// gets serialized into the conversation as a quoted, escape-laden blob, which costs tokens and
    /// buries the data one level deeper than the model expects.
    /// </remarks>
    public async Task<JsonElement> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken) =>
        (await SendCoreAsync(HttpMethod.Get, relativeUrl, body: null, idempotencyKey: null, cancellationToken)).Payload;

    /// <summary>
    /// Performs a write against our own API and reports the status alongside the payload.
    /// </summary>
    /// <remarks>
    /// The idempotency key is not optional: it is the execution's own identity, so a retry — by the
    /// model, by the middleware, or by a user clicking approve twice — replays the first answer
    /// instead of paying an invoice again because a socket hiccuped.
    /// </remarks>
    public Task<ApiResponse> SendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendCoreAsync(method, relativeUrl, body, idempotencyKey, cancellationToken);

    private async Task<ApiResponse> SendCoreAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(ResolveBaseAddress(), relativeUrl);

        using var activity = AssistantTelemetry.Source.StartActivity("assistant.api_call");
        activity?.SetTag("http.request.method", method.Method);
        activity?.SetTag("url.path", uri.AbsolutePath);

        using var request = new HttpRequestMessage(method, uri);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: RequestJson);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = (int)response.StatusCode;
        activity?.SetTag("http.response.status_code", status);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Tool call to {Path} was rejected with {Status}", uri.AbsolutePath, status);
            activity?.SetStatus(ActivityStatusCode.Error, response.StatusCode.ToString());

            var (failure, code) = DescribeFailure(response.StatusCode, responseBody);
            return new ApiResponse(status, failure, code);
        }

        // 204 and an empty 200 are legitimate write responses with nothing to hand the model.
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new ApiResponse(status, JsonSerializer.SerializeToElement(new { status = "ok" }), Code: null);
        }

        if (TryParse(responseBody, out var payload))
        {
            return new ApiResponse(status, payload, Code: null);
        }

        logger.LogError("The response from {Path} was not JSON.", uri.AbsolutePath);
        var (malformed, malformedCode) = DescribeFailure(HttpStatusCode.InternalServerError, string.Empty);
        return new ApiResponse(StatusCodes.Status500InternalServerError, malformed, malformedCode);
    }

    private Uri ResolveBaseAddress()
    {
        if (http.BaseAddress is not null)
        {
            return http.BaseAddress;
        }

        var request = httpContextAccessor.HttpContext?.Request
            ?? throw new InvalidOperationException(
                "No base URL configured and no incoming request to derive one from. Set Assistant:ApiBaseUrl.");

        return new Uri($"{request.Scheme}://{request.Host}");
    }

    private static (JsonElement Payload, string? Code) DescribeFailure(HttpStatusCode statusCode, string body)
    {
        var family = statusCode switch
        {
            HttpStatusCode.Unauthorized => "unauthenticated",
            HttpStatusCode.Forbidden => "forbidden",
            HttpStatusCode.NotFound => "not_found",
            HttpStatusCode.Conflict => "domain_error",
            HttpStatusCode.BadRequest => "invalid_request",
            HttpStatusCode.PreconditionFailed => "resource_changed",
            _ => "api_error",
        };

        var error = new JsonObject
        {
            ["error"] = family,
            ["status"] = (int)statusCode,
        };

        // Business errors arrive as ProblemDetails; lift the useful parts so the model does not have
        // to parse an envelope it was never told about.
        if (TryReadProblemDetails(body, out var code, out var detail))
        {
            if (code is not null)
            {
                error["code"] = code;
            }

            error["detail"] = detail;
        }
        else
        {
            error["detail"] = string.IsNullOrWhiteSpace(body)
                ? "The API rejected the request."
                : body[..Math.Min(body.Length, 500)];
        }

        return (JsonSerializer.SerializeToElement(error), code ?? family);
    }

    private static bool TryParse(string body, out JsonElement payload)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            // Cloned so it outlives the JsonDocument it was parsed from.
            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            payload = default;
            return false;
        }
    }

    private static bool TryReadProblemDetails(string body, out string? code, out string? detail)
    {
        code = null;
        detail = null;

        try
        {
            if (JsonNode.Parse(body) is not JsonObject problem)
            {
                return false;
            }

            code = problem["code"]?.GetValue<string>();
            detail = problem["detail"]?.GetValue<string>() ?? problem["title"]?.GetValue<string>();
            return detail is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Copies the caller's <c>Authorization</c> header onto the outgoing tool request. This one line is
/// the whole "the assistant can never do more than the logged-in user" guarantee.
/// </summary>
public sealed class ForwardCallerIdentityHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var authorization = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();

        if (!string.IsNullOrWhiteSpace(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
