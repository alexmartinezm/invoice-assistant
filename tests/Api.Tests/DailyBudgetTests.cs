using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Support;
using Microsoft.Extensions.AI;

namespace Api.Tests;

/// <summary>
/// The spend kill switch. Each test boots its own app with its own budget, because the
/// budget is global by design — sharing a database would let one test's spend trip another's cap.
/// </summary>
public class DailyBudgetTests
{
    [Fact]
    public async Task With_no_budget_left_chat_answers_429_before_calling_the_model()
    {
        using var factory = new BudgetFactory("0");
        factory.Model.Script([new TextContent("should never be reached")]);

        using var client = await factory.ClientForAsync("ana@demo");
        var response = await client.PostAsJsonAsync("/api/chat", new { message = "hello" });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(factory.Model.ObservedMessages);

        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("daily_budget_exhausted", problem!.Code);
        Assert.Contains("budget", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The budget is checked before every model call, not once per turn: a turn that starts under
    /// the cap and crosses it on a tool round trip is stopped mid-turn, with an error event that
    /// says why rather than a generic failure.
    /// </summary>
    [Fact]
    public async Task A_turn_that_crosses_the_budget_is_stopped_before_its_next_model_call()
    {
        // One scripted call costs 0.0021€, so the first call fits and the second does not.
        using var factory = new BudgetFactory("0.002");
        factory.Model.Script(
            [new FunctionCallContent("call-1", "get_receivables_summary", new Dictionary<string, object?>())],
            [new TextContent("should never be reached")]);

        using var client = await factory.ClientForAsync("ana@demo");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message = "how much are we owed?" }),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var events = await ServerSentEvents.ReadAllAsync(response);
        var error = Assert.Single(events, e => e.Name == "error");
        Assert.Contains("budget", error.Data, StringComparison.OrdinalIgnoreCase);

        // And the next turn does not even start.
        var next = await client.PostAsJsonAsync("/api/chat", new { message = "hello again" });
        Assert.Equal(HttpStatusCode.TooManyRequests, next.StatusCode);
    }

    private sealed class BudgetFactory(string dailyBudgetEur) : ApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
            new Dictionary<string, string?> { ["Usage:DailyBudgetEur"] = dailyBudgetEur };
    }

    private sealed record ProblemPayload(string Title, string Detail, string Code);
}
