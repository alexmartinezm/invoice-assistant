using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Assistant;
using Api.Tests.Support;
using Microsoft.Extensions.AI;

namespace Api.Tests;

/// <summary>
/// What a trace says about a turn. The trace id is shown in the chat footer, so "what did the
/// assistant do, and what did the gate decide?" has to be answerable from the spans alone.
/// </summary>
/// <remarks>
/// Every assertion filters by the turn's own trace id — the same id the footer shows. An
/// <see cref="ActivityListener"/> is process-wide, so without that filter these tests would see the
/// spans of every other test class running in parallel and fail depending on the schedule.
/// </remarks>
public class TelemetryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_turn_records_the_model_call_with_its_tokens_and_cost()
    {
        factory.Model.Script([new TextContent("Hello.")]);

        using var recorded = new RecordedActivities();
        using var client = await factory.ClientForAsync("ana@demo");
        var traceId = await ChatAsync(client, "hi");

        var turn = Assert.Single(recorded.Named("assistant.turn", traceId));
        Assert.Equal("Admin", turn.GetTagItem("assistant.user_role"));

        var call = Assert.Single(recorded.Named("assistant.model_call", traceId));
        Assert.Equal(ScriptedChatClient.ScriptedModelId, call.GetTagItem("assistant.model"));
        Assert.Equal(ScriptedChatClient.PromptTokensPerCall, call.GetTagItem("assistant.prompt_tokens"));
        Assert.Equal(ScriptedChatClient.CompletionTokensPerCall, call.GetTagItem("assistant.completion_tokens"));
        Assert.Equal(0.0021d, Assert.IsType<double>(call.GetTagItem("assistant.cost_eur")), 6);
    }

    /// <summary>
    /// A tool call gets a span carrying the gate's decision — including the limit blocks, which
    /// return before the policy engine runs and so leave no policy span of their own.
    /// </summary>
    [Theory]
    [InlineData("lucia@demo", "cancel_invoice", "denied")]
    [InlineData("ana@demo", "get_receivables_summary", "allowed")]
    [InlineData("ana@demo", "cancel_invoice", "pending_approval")]
    public async Task A_tool_call_span_carries_the_gate_decision(string user, string tool, string expected)
    {
        factory.Model.Script(
            [ToolCall(tool)],
            [new TextContent("Reported.")]);

        using var recorded = new RecordedActivities();
        using var client = await factory.ClientForAsync(user);
        var traceId = await ChatAsync(client, $"please {tool}");

        var span = Assert.Single(recorded.Named("assistant.tool_call", traceId));
        Assert.Equal(tool, span.GetTagItem("assistant.tool"));
        Assert.Equal(expected, span.GetTagItem("assistant.gate_decision"));
    }

    [Fact]
    public async Task A_call_stopped_by_a_per_turn_limit_is_still_explained_by_its_span()
    {
        // The second write in one turn is blocked by maxWritesPerTurn without ever reaching policy.
        factory.Model.Script(
            [ToolCall("cancel_invoice"), ToolCall("cancel_invoice")],
            [new TextContent("Only one of those went through.")]);

        using var recorded = new RecordedActivities();
        using var client = await factory.ClientForAsync("ana@demo");
        var traceId = await ChatAsync(client, "cancel both invoices");

        var decisions = recorded.Named("assistant.tool_call", traceId)
            .Select(span => span.GetTagItem("assistant.gate_decision"))
            .ToList();

        Assert.Equal(["pending_approval", "blocked"], decisions);
    }

    private static FunctionCallContent ToolCall(string name) => new(
        Guid.NewGuid().ToString("N"),
        name,
        new Dictionary<string, object?> { ["number"] = "2026-0001" });

    /// <summary>Runs a turn and returns its trace id, as the <c>conversation</c> event reports it.</summary>
    private static async Task<string> ChatAsync(HttpClient client, string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message }),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var events = await ServerSentEvents.ReadAllAsync(response);
        var traceId = JsonSerializer
            .Deserialize<ConversationPayload>(events[0].Data, JsonSerializerOptions.Web)!.TraceId;

        Assert.NotEmpty(traceId);
        return traceId;
    }

    private sealed record ConversationPayload(Guid ConversationId, string TraceId);

    /// <summary>
    /// Collects the assistant's finished activities. Sampling is forced on: without a listener that
    /// returns <see cref="ActivitySamplingResult.AllDataAndRecorded"/>, <c>StartActivity</c> hands
    /// back null and every assertion here would pass by never running.
    /// </summary>
    private sealed class RecordedActivities : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _activities = [];
        private readonly Lock _gate = new();

        public RecordedActivities()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == AssistantTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _activities.Add(activity);
                    }
                },
            };

            ActivitySource.AddActivityListener(_listener);
        }

        /// <summary>Spans of one operation belonging to one turn, in the order they finished.</summary>
        public List<Activity> Named(string operationName, string traceId)
        {
            lock (_gate)
            {
                return
                [
                    .. _activities.Where(activity =>
                        activity.OperationName == operationName
                        && activity.TraceId.ToString() == traceId),
                ];
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
