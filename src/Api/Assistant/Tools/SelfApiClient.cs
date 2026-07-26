using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Api.Assistant.Tools;

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
    public Task<JsonElement> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, relativeUrl, body: null, idempotencyKey: null, cancellationToken);

    /// <summary>
    /// Performs a write against our own API.
    /// </summary>
    /// <remarks>
    /// The idempotency key is not optional in practice: a tool call can be retried by the model,
    /// by the middleware, or by a user clicking approve twice, and "send the invoice" is not
    /// something to do twice because a socket hiccuped.
    /// </remarks>
    public Task<JsonElement> SendJsonAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendAsync(method, relativeUrl, body, idempotencyKey, cancellationToken);

    private async Task<JsonElement> SendAsync(
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
        activity?.SetTag("http.response.status_code", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Tool call to {Path} was rejected with {Status}", uri.AbsolutePath, (int)response.StatusCode);
            activity?.SetStatus(ActivityStatusCode.Error, response.StatusCode.ToString());
            return DescribeFailure(response.StatusCode, responseBody);
        }

        // 204 and an empty 200 are legitimate write responses with nothing to hand the model.
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return JsonSerializer.SerializeToElement(new { status = "ok" });
        }

        if (TryParse(responseBody, out var payload))
        {
            return payload;
        }

        logger.LogError("The response from {Path} was not JSON.", uri.AbsolutePath);
        return DescribeFailure(HttpStatusCode.InternalServerError, string.Empty);
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

    private static JsonElement DescribeFailure(HttpStatusCode statusCode, string body)
    {
        var error = new JsonObject
        {
            ["error"] = statusCode switch
            {
                HttpStatusCode.Unauthorized => "unauthenticated",
                HttpStatusCode.Forbidden => "forbidden",
                HttpStatusCode.NotFound => "not_found",
                HttpStatusCode.Conflict => "domain_error",
                HttpStatusCode.BadRequest => "invalid_request",
                _ => "api_error",
            },
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

        return JsonSerializer.SerializeToElement(error);
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
