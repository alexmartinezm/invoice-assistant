using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace InvoiceAssistant.Evals;

/// <summary>
/// The harness, run against a scripted model. These tests need no credentials and no provider:
/// they prove the machinery — placeholder resolution, the SSE turn, the database diff, the
/// expectation checks, the retry — so that the first real run in CI is not also the first run
/// ever. What they can never prove is model behaviour; that is what the real cases are for.
/// </summary>
public sealed class HarnessSelfTests : IClassFixture<HarnessSelfTests.ScriptedEvalHost>
{
    private readonly ScriptedEvalHost _host;
    private readonly EvalRunner _runner;

    public HarnessSelfTests(ScriptedEvalHost host)
    {
        _host = host;
        _runner = new EvalRunner(host);
    }

    [Fact]
    public async Task A_read_case_passes_when_the_scripted_model_calls_the_expected_tool()
    {
        _host.Model.Script(
            [Call("call-1", "list_invoices", new() { ["overdue_only"] = true })],
            [new TextContent("There are overdue invoices.")]);

        var result = await _runner.RunAsync(EvalCases.All["read-overdue-01"]);

        Assert.True(result.Passed, result.Failure);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task A_write_case_passes_when_the_write_ends_as_a_pending_action()
    {
        var number = await new Placeholders(_host).ApplyAsync("{{a_sent_invoice_over_100}}");

        _host.Model.Script(
            [Call("call-1", "mark_invoice_paid", new() { ["number"] = number })],
            [new TextContent("This is waiting for approval.")]);

        var result = await _runner.RunAsync(EvalCases.All["write-confirm-01"]);

        Assert.True(result.Passed, result.Failure);
    }

    [Fact]
    public async Task A_case_fails_with_the_observed_facts_after_one_retry()
    {
        _host.Model.Script([new TextContent("I would rather not use any tool.")]);

        var result = await _runner.RunAsync(EvalCases.All["read-overdue-01"]);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("none of the accepted tool calls was made", result.Failure);
        Assert.Contains("Tool calls: (none)", result.Failure);
    }

    /// <summary>
    /// Every placeholder in every shipped case resolves against the seeded database. A case that
    /// names an unknown placeholder — or a seed change that empties a selector — fails here, on
    /// every PR, instead of only on runs that have an API key.
    /// </summary>
    [Fact]
    public async Task Every_shipped_case_resolves_its_placeholders()
    {
        var placeholders = new Placeholders(_host);

        foreach (var evalCase in EvalCases.All.Values)
        {
            var prompt = await placeholders.ApplyAsync(evalCase.Prompt);
            Assert.DoesNotContain("{{", prompt);

            foreach (var value in evalCase.Expect.AnyOf.SelectMany(a => a.ArgsMatch.Values))
            {
                var resolved = await placeholders.ResolveExpectedValueAsync(value);
                Assert.DoesNotContain("{{", resolved ?? string.Empty);
                Assert.NotEqual("$today", resolved);
            }
        }
    }

    private static FunctionCallContent Call(string id, string name, Dictionary<string, object?> args) =>
        new(id, name, args);

    /// <summary>An <see cref="EvalHost"/> whose "provider" is a queue of scripted turns.</summary>
    public sealed class ScriptedEvalHost : EvalHost
    {
        public ScriptedEvalHost()
            : this(new ScriptedModel())
        {
        }

        private ScriptedEvalHost(ScriptedModel model)
            : base(model)
        {
            Model = model;
        }

        public ScriptedModel Model { get; }
    }

    /// <summary>
    /// The smallest scripted <see cref="IChatClient"/> that satisfies the function-invoking
    /// middleware: one queued content list per round trip, empty text once the queue runs dry.
    /// </summary>
    public sealed class ScriptedModel : IChatClient
    {
        private readonly Queue<IReadOnlyList<AIContent>> _turns = new();

        public void Script(params IReadOnlyList<AIContent>[] turns)
        {
            _turns.Clear();

            foreach (var turn in turns)
            {
                _turns.Enqueue(turn);
            }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatMessage> reply = [];

            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                reply.Add(new ChatMessage(ChatRole.Assistant, [.. update.Contents]));
            }

            return new ChatResponse(reply);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var contents = _turns.Count > 0
                ? _turns.Dequeue()
                : [new TextContent("(script exhausted)")];

            yield return new ChatResponseUpdate(ChatRole.Assistant, [.. contents]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
