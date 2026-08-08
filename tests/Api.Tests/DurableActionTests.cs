using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Domain;
using Api.Features.Invoices;
using Api.Infrastructure;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>
/// What happens when two callers reach the same approval at the same moment, and what happens when
/// one caller reaches it twice.
/// </summary>
/// <remarks>
/// These run real concurrent HTTP requests against a real PostgreSQL. Nothing here is proved with a
/// sleep: the assertions are on row counts and on the invoice, because a race that only fails one
/// run in fifty is a race that passes CI and breaks production.
/// </remarks>
public class DurableActionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>
    /// The headline. Twenty tabs, one decision, one execution, one paid invoice — and every caller
    /// that got an answer got the <em>same</em> execution identity, so none of them is looking at a
    /// different story about the same money.
    /// </summary>
    [Fact]
    public async Task Twenty_concurrent_approvals_produce_one_decision_and_one_execution()
    {
        var number = await CreateAndSendAsync(unitPrice: 500m);
        var actionId = await ProposeAsync(number);

        using var client = await factory.ClientForAsync("carlos@demo");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            client.PostAsync($"/api/actions/{actionId}/approve", content: null)));

        var succeeded = responses.Where(r => r.IsSuccessStatusCode).ToList();
        Assert.NotEmpty(succeeded);

        var outcomes = new List<OutcomePayload>();
        foreach (var response in succeeded)
        {
            outcomes.Add((await response.Content.ReadFromJsonAsync<OutcomePayload>())!);
        }

        // One execution identity, however many callers were told about it.
        Assert.Single(outcomes.Select(o => o.ExecutionId).Distinct());

        Assert.Equal(InvoiceStatus.Paid, await StatusOfAsync(number));
        Assert.Equal(1, await ExecutionCountAsync(actionId));
        Assert.Equal(1, await ConfirmedAuditCountAsync(actionId));

        // And exactly one request went out. Nineteen callers were handed the state instead of
        // being allowed to race the twentieth's idempotency key.
        Assert.Equal(1, await AttemptCountAsync(actionId));

        // One "Done" line, not twenty. The transcript is what the next turn reads back.
        Assert.Single(await MessagesForAsync(actionId), m => m.StartsWith("Done:", StringComparison.Ordinal));

        var action = await ActionAsync(actionId);
        Assert.Equal(PendingActionStatus.Approved, action.Status);
        Assert.Equal(ActionResolutionReason.UserApproved, action.ResolutionReason);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// The sequential half of the same guarantee: approving twice is a retry, not a second write.
    /// The second call replays the stored answer instead of the 409 the old code returned — a
    /// client that lost the first response is not doing anything wrong.
    /// </summary>
    [Fact]
    public async Task Approving_twice_replays_the_first_answer_rather_than_repeating_the_write()
    {
        var number = await CreateAndSendAsync(unitPrice: 500m);
        var actionId = await ProposeAsync(number);

        using var client = await factory.ClientForAsync("carlos@demo");

        using var first = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);
        using var second = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var one = await first.Content.ReadFromJsonAsync<OutcomePayload>();
        var two = await second.Content.ReadFromJsonAsync<OutcomePayload>();

        Assert.Equal(one!.ExecutionId, two!.ExecutionId);
        Assert.Equal(one.Message, two.Message);
        Assert.Equal("succeeded", two.ExecutionStatus);

        Assert.Equal(1, await ExecutionCountAsync(actionId));
        Assert.Equal(1, await AttemptCountAsync(actionId));

        // And the transcript says it once. A replayed approval that appended a second "Done" line
        // would make the conversation claim two settlements.
        var messages = await MessagesForAsync(actionId);
        Assert.Single(messages, message => message.StartsWith("Done:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Approve and reject arriving together. Exactly one of them decides; the loser is shown what
    /// was decided rather than being allowed to overwrite it.
    /// </summary>
    [Fact]
    public async Task An_approve_and_a_reject_racing_produce_exactly_one_resolution()
    {
        var number = await CreateAndSendAsync(unitPrice: 500m);
        var actionId = await ProposeAsync(number);

        using var client = await factory.ClientForAsync("carlos@demo");

        var approve = client.PostAsync($"/api/actions/{actionId}/approve", content: null);
        var reject = client.PostAsync($"/api/actions/{actionId}/reject", content: null);

        using var approved = await approve;
        using var rejected = await reject;

        var winners = new[] { approved, rejected }.Count(r => r.IsSuccessStatusCode);
        Assert.Equal(1, winners);

        var action = await ActionAsync(actionId);
        Assert.Contains(action.Status, new[] { PendingActionStatus.Approved, PendingActionStatus.Rejected });

        // The invoice and the ledger agree with the row: approved means one execution and a paid
        // invoice, rejected means neither.
        if (action.Status is PendingActionStatus.Approved)
        {
            Assert.Equal(InvoiceStatus.Paid, await StatusOfAsync(number));
            Assert.Equal(1, await ExecutionCountAsync(actionId));
        }
        else
        {
            Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));
            Assert.Equal(0, await ExecutionCountAsync(actionId));
        }
    }

    /// <summary>
    /// Two people, one proposal. The winner is bound as the actor; the loser cannot pick the
    /// execution up under their own identity, because an execution runs as somebody.
    /// </summary>
    [Fact]
    public async Task The_loser_of_a_two_approver_race_cannot_resume_under_their_own_identity()
    {
        var number = await CreateAndSendAsync(unitPrice: 500m);
        var actionId = await ProposeAsync(number);

        using var accountant = await factory.ClientForAsync("carlos@demo");
        using var admin = await factory.ClientForAsync("ana@demo");

        using var first = await accountant.PostAsync($"/api/actions/{actionId}/approve", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await admin.PostAsync($"/api/actions/{actionId}/approve", content: null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var problem = await second.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("action_not_open", problem!.Code);

        var execution = await ExecutionAsync(actionId);
        var carlos = await UserIdAsync("carlos@demo");
        Assert.Equal(carlos, execution.UserId);
        Assert.Equal(1, execution.AttemptCount);
    }

    /// <summary>
    /// A policy-allowed write gets an execution too. Without it, "what did the assistant attempt
    /// today?" would only be answerable for the writes somebody was asked about.
    /// </summary>
    [Fact]
    public async Task An_auto_allowed_write_also_gets_a_durable_execution()
    {
        var number = await CreateAndSendAsync(unitPrice: 50m);

        factory.Model.Script(
            [Call("mark_invoice_paid", new { number })],
            [new TextContent("Done.")]);

        using var client = await factory.ClientForAsync("carlos@demo");
        var conversationId = await ChatAsync(client, $"mark {number} as paid");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var execution = await db.ActionExecutions.AsNoTracking()
            .SingleAsync(e => e.ConversationId == conversationId);

        Assert.Null(execution.PendingActionId);
        Assert.Equal(ExecutionDecision.Auto, execution.Decision);
        Assert.Equal(ActionExecutionStatus.Succeeded, execution.Status);
        Assert.Equal("mark_invoice_paid", execution.ToolName);

        // The audit row points at it, so the decision and the attempt are one join apart.
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.ConversationId == conversationId);
        Assert.Equal(AuditDecision.Auto, audit.Decision);
        Assert.Equal(execution.Id, audit.ExecutionId);
    }

    /// <summary>An expired proposal records why it closed, so a queue that dropped it can say so.</summary>
    [Fact]
    public async Task An_expired_action_records_its_reason_and_executes_nothing()
    {
        var number = await CreateAndSendAsync(unitPrice: 500m);
        var actionId = await ProposeAsync(number);
        await ExpireAsync(actionId);

        using var client = await factory.ClientForAsync("carlos@demo");
        using var response = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var action = await ActionAsync(actionId);
        Assert.Equal(PendingActionStatus.Expired, action.Status);
        Assert.Equal(ActionResolutionReason.Expired, action.ResolutionReason);
        Assert.Equal(0, await ExecutionCountAsync(actionId));
        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));
    }

    // --- helpers ---------------------------------------------------------------------------------

    private async Task<string> CreateAndSendAsync(decimal unitPrice)
    {
        using var client = await factory.ClientForAsync("carlos@demo");

        var created = await client.PostWriteAsync("/api/invoices", new
        {
            customerName = "Delta Logística",
            lines = new[] { new { description = "Durable action test", quantity = 1, unitPrice } },
        });
        created.EnsureSuccessStatusCode();

        var invoice = await created.Content.ReadFromJsonAsync<InvoiceDetail>();
        (await client.PostWriteAsync($"/api/invoices/{invoice!.Number}/send")).EnsureSuccessStatusCode();

        return invoice.Number;
    }

    private async Task<Guid> ProposeAsync(string number)
    {
        factory.Model.Script([Call("mark_invoice_paid", new { number })], [new TextContent("Confirm?")]);

        using var client = await factory.ClientForAsync("carlos@demo");
        var events = await ChatEventsAsync(client, $"mark {number} as paid");

        return Parse<ApprovalPayload>(events.Single(e => e.Name == "approval_required").Data).ActionId;
    }

    private async Task ExpireAsync(Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlAsync(
            $"""UPDATE pending_actions SET "ExpiresAt" = {factory.Clock.UtcNow.AddMinutes(-1)} WHERE "Id" = {actionId}""");
    }

    private async Task<PendingAction> ActionAsync(Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.PendingActions.AsNoTracking().SingleAsync(a => a.Id == actionId);
    }

    private async Task<ActionExecution> ExecutionAsync(Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.ActionExecutions.AsNoTracking().SingleAsync(e => e.PendingActionId == actionId);
    }

    private async Task<int> ExecutionCountAsync(Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.ActionExecutions.AsNoTracking().CountAsync(e => e.PendingActionId == actionId);
    }

    private async Task<int> AttemptCountAsync(Guid actionId) => (await ExecutionAsync(actionId)).AttemptCount;

    private async Task<int> ConfirmedAuditCountAsync(Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var executionIds = await db.ActionExecutions.AsNoTracking()
            .Where(e => e.PendingActionId == actionId)
            .Select(e => (Guid?)e.Id)
            .ToListAsync();

        return await db.AuditEvents.AsNoTracking()
            .CountAsync(a => a.Decision == AuditDecision.Confirmed && executionIds.Contains(a.ExecutionId));
    }

    private async Task<List<string>> MessagesForAsync(Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversationId = await db.PendingActions.AsNoTracking()
            .Where(a => a.Id == actionId)
            .Select(a => a.ConversationId)
            .SingleAsync();

        return await db.Set<Message>().AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .Select(m => m.Content)
            .ToListAsync();
    }

    private async Task<InvoiceStatus> StatusOfAsync(string number)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.Number == number)
            .Select(invoice => invoice.Status)
            .SingleAsync();
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    private static async Task<Guid> ChatAsync(HttpClient client, string message) =>
        Parse<ConversationPayload>((await ChatEventsAsync(client, message))[0].Data).ConversationId;

    private static async Task<List<ServerSentEvent>> ChatEventsAsync(HttpClient client, string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message }),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        return await ServerSentEvents.ReadAllAsync(response);
    }

    private static FunctionCallContent Call(string name, object arguments)
    {
        var json = JsonSerializer.SerializeToElement(arguments, JsonSerializerOptions.Web);

        return new FunctionCallContent(
            Guid.NewGuid().ToString("N"),
            name,
            json.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.GetString()));
    }

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!;

    private sealed record ConversationPayload(Guid ConversationId, string TraceId);

    private sealed record ApprovalPayload(Guid ActionId, string Tool, string Summary);

    private sealed record OutcomePayload(
        Guid ActionId,
        Guid? ExecutionId,
        string DecisionStatus,
        string? ExecutionStatus,
        string Summary,
        string Message);

    private sealed record ProblemPayload(string? Code, string? Detail);
}
