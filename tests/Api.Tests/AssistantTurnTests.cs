using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Assistant;
using Api.Assistant.Tools;
using Api.Domain;
using Api.Infrastructure;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>
/// A chat turn end to end with a scripted model: the tool call really leaves over HTTP, really
/// comes back in through the API's authorization, and what the model gets to see is whatever the
/// endpoint decided for that user.
/// </summary>
public class AssistantTurnTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_turn_streams_the_tool_activity_and_then_the_answer()
    {
        factory.Model.Script(
            [ToolCall("call-1", "get_receivables_summary")],
            [new TextContent("You are owed €71,546.20 in total.")]);

        using var client = await factory.ClientForAsync("ana@demo");
        var events = await ChatAsync(client, "how much are we owed?");

        Assert.Equal("conversation", events[0].Name);
        Assert.Equal("done", events[^1].Name);
        Assert.DoesNotContain(factory.Logs.Failures, failure => failure.Contains("Chat turn failed"));

        var activity = events.Where(e => e.Name == "activity").Select(e => Parse<ToolActivityPayload>(e.Data)).ToList();
        Assert.Equal(["start", "end"], activity.Select(a => a.Phase));
        Assert.All(activity, a => Assert.Equal("get_receivables_summary", a.Tool));

        var answer = string.Concat(events.Where(e => e.Name == "token").Select(e => Parse<TokenPayload>(e.Data).Text));
        Assert.Equal("You are owed €71,546.20 in total.", answer);
    }

    /// <summary>
    /// The whole point of ADR 002. If the caller's token were not forwarded on the tool's HTTP
    /// call, the API would answer 401 and the model would see an error instead of the ledger.
    /// </summary>
    [Fact]
    public async Task The_tool_call_reaches_the_api_as_the_logged_in_user()
    {
        factory.Model.Script(
            [ToolCall("call-1", "list_invoices", new Dictionary<string, object?> { ["overdue_only"] = true })],
            [new TextContent("There are overdue invoices.")]);

        using var client = await factory.ClientForAsync("lucia@demo");
        await ChatAsync(client, "which invoices are overdue?");

        var toolResult = factory.Model.ObservedMessages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single();

        // The tool hands the model the endpoint's own JSON, so the result parses as the same shape
        // `GET /api/invoices` returns — not an auth error, and not a stringified blob.
        var payload = Assert.IsType<JsonElement>(toolResult.Result);
        Assert.False(payload.TryGetProperty("error", out _), payload.ToString());

        var invoices = payload.GetProperty("invoices").EnumerateArray().ToList();
        Assert.NotEmpty(invoices);
        Assert.All(invoices, invoice => Assert.True(invoice.GetProperty("isOverdue").GetBoolean()));
    }

    /// <summary>
    /// The capability boundary: what the assistant can do is the length of this list, not the
    /// wording of the prompt.
    /// </summary>
    /// <remarks>
    /// Until F2 this asserted that every tool was a read. Now that writes exist the boundary is
    /// stated as what is still missing: nothing deletes, nothing acts on more than one named
    /// invoice, and nothing touches users, roles or configuration. Those are the capabilities a
    /// prompt injection would need, and they are absent from the catalog rather than discouraged.
    /// </remarks>
    [Fact]
    public async Task The_model_is_only_ever_offered_the_declared_catalog()
    {
        factory.Model.Script([new TextContent("Nothing to do.")]);

        using var client = await factory.ClientForAsync("ana@demo");
        await ChatAsync(client, "hello");

        string[] expected =
        [
            "list_invoices", "get_invoice", "search_customers", "get_receivables_summary",
            "create_draft_invoice", "send_invoice", "mark_invoice_paid", "cancel_invoice", "update_due_date",
        ];

        Assert.Equal(expected, factory.Model.OfferedTools.OfType<AIFunction>().Select(tool => tool.Name));

        using var scope = factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ToolCatalog>();

        Assert.DoesNotContain(catalog.Tools, tool =>
            tool.Name.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || tool.Name.Contains("bulk", StringComparison.OrdinalIgnoreCase)
            || tool.Name.Contains("all", StringComparison.OrdinalIgnoreCase)
            || tool.Name.Contains("user", StringComparison.OrdinalIgnoreCase)
            || tool.Name.Contains("role", StringComparison.OrdinalIgnoreCase)
            || tool.Name.Contains("config", StringComparison.OrdinalIgnoreCase));

        // Every write is reachable only through the gate, so none of them can be a bare Viewer call.
        Assert.All(
            catalog.Tools.Where(tool => tool.SideEffect is ToolSideEffect.Write),
            tool => Assert.True(tool.RequiredRole >= Role.Accountant, tool.Name));
    }

    [Fact]
    public async Task The_conversation_records_the_prompt_hash_it_ran_under()
    {
        factory.Model.Script([new TextContent("Hello.")]);

        using var client = await factory.ClientForAsync("carlos@demo");
        var events = await ChatAsync(client, "hi");
        var conversationId = Parse<ConversationPayload>(events[0].Data).ConversationId;

        using var scope = factory.Services.CreateScope();
        var expectedHash = scope.ServiceProvider.GetRequiredService<SystemPromptProvider>().Hash;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations
            .Include(c => c.Messages)
            .SingleAsync(c => c.Id == conversationId);

        Assert.Equal(expectedHash, conversation.SystemPromptHash);
        Assert.Equal(["hi", "Hello."], conversation.Messages.OrderBy(m => m.CreatedAt).Select(m => m.Content));
    }

    [Fact]
    public async Task The_turn_starts_from_the_versioned_system_prompt()
    {
        factory.Model.Script([new TextContent("Hello.")]);

        using var client = await factory.ClientForAsync("ana@demo");
        await ChatAsync(client, "hi");

        using var scope = factory.Services.CreateScope();
        var systemPrompt = scope.ServiceProvider.GetRequiredService<SystemPromptProvider>();
        var first = factory.Model.ObservedMessages[0];

        Assert.Equal(ChatRole.System, first.Role);
        Assert.Equal(systemPrompt.Text, first.Text);
    }

    [Fact]
    public async Task An_oversized_message_is_rejected_before_the_model_is_called()
    {
        factory.Model.Script([new TextContent("should never be reached")]);

        using var client = await factory.ClientForAsync("ana@demo");
        var response = await client.PostAsJsonAsync("/api/chat", new { message = new string('a', 5_000) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Model.ObservedMessages);
    }

    [Fact]
    public async Task Chat_needs_an_authenticated_caller()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chat", new { message = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<List<ServerSentEvent>> ChatAsync(HttpClient client, string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message }),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        return await ServerSentEvents.ReadAllAsync(response);
    }

    private static FunctionCallContent ToolCall(string callId, string name, IDictionary<string, object?>? arguments = null) =>
        new(callId, name, arguments ?? new Dictionary<string, object?>());

    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!;

    private sealed record ConversationPayload(Guid ConversationId, string TraceId);

    private sealed record ToolActivityPayload(string Tool, string Phase, string Label);

    private sealed record TokenPayload(string Text);
}
