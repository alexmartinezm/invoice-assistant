namespace InvoiceAssistant.Evals;

public sealed record ServerSentEvent(string Name, string Data);

/// <summary>Reads an SSE response to completion. Enough for the harness; not a general-purpose client.</summary>
public static class ServerSentEvents
{
    public static async Task<List<ServerSentEvent>> ReadAllAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        List<ServerSentEvent> events = [];
        string? name = null;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                events.Add(new ServerSentEvent(name ?? "message", line["data:".Length..].Trim()));
                name = null;
            }
        }

        return events;
    }
}
