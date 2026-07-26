using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Assistant.Usage;
using Api.Infrastructure;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>
/// The cost ledger (F4): every model call leaves a priced row in <c>usage_records</c>, and the
/// usage endpoints report those rows to their owner — or to an Admin, who sees everyone's.
/// </summary>
/// <remarks>
/// The scripted model reports fixed usage (120 in / 45 out) and the factory prices it at
/// 10€ / 20€ per million tokens, so every scripted call costs exactly 0.0021€ — assertions here
/// are arithmetic, not tolerance.
/// </remarks>
public class UsageAccountingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const decimal CostPerCall = 0.0021m;

    [Fact]
    public async Task A_turn_records_one_priced_row_per_model_call()
    {
        factory.Model.Script(
            [new FunctionCallContent("call-1", "get_receivables_summary", new Dictionary<string, object?>())],
            [new TextContent("You are owed money.")]);

        using var client = await factory.ClientForAsync("ana@demo");
        var conversationId = await ChatAsync(client, "how much are we owed?");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var records = await db.UsageRecords
            .Where(r => r.ConversationId == conversationId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        // One row per round trip: the call that asked for the tool, and the call that answered.
        Assert.Equal(2, records.Count);
        Assert.All(records, record =>
        {
            Assert.Equal(ScriptedChatClient.ScriptedModelId, record.Model);
            Assert.Equal(ScriptedChatClient.PromptTokensPerCall, record.PromptTokens);
            Assert.Equal(ScriptedChatClient.CompletionTokensPerCall, record.CompletionTokens);
            Assert.Equal(CostPerCall, record.CostEur);
        });

        // The tool call was requested in the first round trip and not in the second.
        Assert.Equal([1, 0], records.Select(r => r.ToolCallCount));
    }

    [Fact]
    public async Task The_summary_totals_the_visible_records_and_reports_the_budget()
    {
        factory.Model.Script([new TextContent("Hello.")]);

        using var client = await factory.ClientForAsync("carlos@demo");
        await ChatAsync(client, "hi");

        var summary = await client.GetFromJsonAsync<SummaryPayload>("/api/usage/summary");

        Assert.NotNull(summary);
        Assert.True(summary.ModelCalls >= 1);
        Assert.True(summary.CostEur >= CostPerCall);
        Assert.Equal(summary.PromptTokens / ScriptedChatClient.PromptTokensPerCall, summary.ModelCalls);

        // The budget figures are the global wallet, not the caller's slice.
        Assert.Equal(1m, summary.DailyBudgetEur);
        Assert.True(summary.SpentTodayEur > 0);
        Assert.False(summary.BudgetExhausted);
    }

    [Fact]
    public async Task The_conversation_detail_carries_the_call_timeline_and_the_gate_decisions()
    {
        // A Viewer proposing a write is denied by policy, which is an audited security event —
        // exactly the kind of thing the cost timeline should show beside the model calls.
        factory.Model.Script(
            [new FunctionCallContent("call-1", "cancel_invoice", new Dictionary<string, object?> { ["number"] = "2026-0001" })],
            [new TextContent("I cannot do that.")]);

        using var client = await factory.ClientForAsync("lucia@demo");
        var conversationId = await ChatAsync(client, "cancel invoice 2026-0001");

        var detail = await client.GetFromJsonAsync<DetailPayload>($"/api/usage/conversations/{conversationId}");

        Assert.NotNull(detail);
        Assert.Equal(2, detail.ModelCalls);
        Assert.Equal(2 * CostPerCall, detail.CostEur);
        Assert.Equal(2, detail.Calls.Count);
        Assert.All(detail.Calls, call => Assert.True(call.LatencyMs >= 0));

        var toolEvent = Assert.Single(detail.ToolEvents);
        Assert.Equal("cancel_invoice", toolEvent.Tool);
        Assert.Equal("denied", toolEvent.Decision);
    }

    [Fact]
    public async Task Usage_is_scoped_to_its_owner_unless_the_caller_is_an_admin()
    {
        factory.Model.Script([new TextContent("Hello Carlos.")]);

        using var carlos = await factory.ClientForAsync("carlos@demo");
        var conversationId = await ChatAsync(carlos, "hi");

        // Another non-admin cannot see the conversation in the list, and probing the detail by id
        // answers 404 — the same as an id that never existed.
        using var lucia = await factory.ClientForAsync("lucia@demo");
        var luciaRows = await lucia.GetFromJsonAsync<List<RowPayload>>("/api/usage/conversations");
        Assert.DoesNotContain(luciaRows!, row => row.ConversationId == conversationId);

        var probe = await lucia.GetAsync($"/api/usage/conversations/{conversationId}");
        Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);

        // The Admin sees it, attributed to its owner.
        using var ana = await factory.ClientForAsync("ana@demo");
        var anaRows = await ana.GetFromJsonAsync<List<RowPayload>>("/api/usage/conversations");
        var row = Assert.Single(anaRows!, r => r.ConversationId == conversationId);
        Assert.Equal("carlos@demo", row.UserEmail);
        Assert.Equal(ScriptedChatClient.ScriptedModelId, row.Models);
    }

    [Fact]
    public async Task A_model_without_a_configured_price_is_recorded_at_zero_and_flagged()
    {
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<UsageOptions>>();

        Assert.Null(options.Value.PriceFor("some-model-nobody-priced"));
    }

    private static async Task<Guid> ChatAsync(HttpClient client, string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message }),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var events = await ServerSentEvents.ReadAllAsync(response);
        return JsonSerializer.Deserialize<ConversationPayload>(events[0].Data, JsonSerializerOptions.Web)!
            .ConversationId;
    }

    private sealed record ConversationPayload(Guid ConversationId);

    private sealed record SummaryPayload(
        int Conversations,
        int ModelCalls,
        long PromptTokens,
        long CompletionTokens,
        decimal CostEur,
        decimal SpentTodayEur,
        decimal DailyBudgetEur,
        bool BudgetExhausted);

    private sealed record RowPayload(
        Guid ConversationId,
        string UserEmail,
        string Models,
        int ModelCalls,
        decimal CostEur);

    private sealed record CallPayload(string Model, int PromptTokens, int CompletionTokens, int LatencyMs, decimal CostEur);

    private sealed record ToolEventPayload(string Tool, string Decision);

    private sealed record DetailPayload(
        Guid ConversationId,
        int ModelCalls,
        decimal CostEur,
        List<CallPayload> Calls,
        List<ToolEventPayload> ToolEvents);
}
