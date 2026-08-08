using System.Net.Http.Json;

namespace Api.Tests.Support;

/// <summary>
/// Invoice writes, with the <c>Idempotency-Key</c> the API now requires.
/// </summary>
/// <remarks>
/// Every caller generates one key per user action, which is what a real client does; a test that
/// wants to prove replay passes the same key twice on purpose. The helper exists so that "did this
/// test remember the header?" is never the reason a suite goes red.
/// </remarks>
public static class WriteRequests
{
    public static Task<HttpResponseMessage> PostWriteAsync(
        this HttpClient client,
        string url,
        object? body = null,
        string? key = null,
        string? ifMatch = null) =>
        client.SendWriteAsync(HttpMethod.Post, url, body, key, ifMatch);

    public static Task<HttpResponseMessage> PatchWriteAsync(
        this HttpClient client,
        string url,
        object? body = null,
        string? key = null,
        string? ifMatch = null) =>
        client.SendWriteAsync(HttpMethod.Patch, url, body, key, ifMatch);

    public static async Task<HttpResponseMessage> SendWriteAsync(
        this HttpClient client,
        HttpMethod method,
        string url,
        object? body = null,
        string? key = null,
        string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Idempotency-Key", key ?? Guid.CreateVersion7().ToString());

        if (ifMatch is not null)
        {
            request.Headers.Add("If-Match", ifMatch);
        }

        return await client.SendAsync(request);
    }
}
