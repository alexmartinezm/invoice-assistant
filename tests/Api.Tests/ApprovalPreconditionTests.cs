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
/// Binding an approval to the state it was proposed against.
/// </summary>
/// <remarks>
/// The arguments being frozen was never enough. "Mark 2026-0041 as paid" agreed to five minutes ago
/// is not the same instruction if somebody has changed the invoice since — the words match and the
/// situation does not, which is the shape of every convincing near-miss in this area.
/// </remarks>
public class ApprovalPreconditionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task An_invoice_reports_its_revision_and_an_etag_that_agree()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateAndSendAsync(unitPrice: 40m);

        using var response = await client.GetAsync($"/api/invoices/{number}");
        var invoice = await response.Content.ReadFromJsonAsync<InvoiceDetail>();

        // Created at 1, sent takes it to 2. Every mutation moves it, which is what makes it usable
        // as a precondition rather than as decoration.
        Assert.Equal(2, invoice!.Revision);
        Assert.Equal("\"2\"", response.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task A_write_against_the_current_revision_is_allowed()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateAndSendAsync(unitPrice: 40m);

        using var response = await client.PostWriteAsync($"/api/invoices/{number}/mark-paid", ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var invoice = await response.Content.ReadFromJsonAsync<InvoiceDetail>();
        Assert.Equal(3, invoice!.Revision);
    }

    [Fact]
    public async Task A_write_against_a_stale_revision_is_refused_and_changes_nothing()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateAndSendAsync(unitPrice: 40m);

        using var response = await client.PostWriteAsync($"/api/invoices/{number}/mark-paid", ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("resource_changed", problem!.Code);

        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));
    }

    /// <summary>
    /// The scenario the precondition exists for, end to end: a proposal is made, somebody else
    /// changes the invoice inside the five-minute window, and the approval refuses rather than
    /// landing on state the approver never saw.
    /// </summary>
    [Fact]
    public async Task An_approval_whose_invoice_changed_refuses_and_asks_for_a_fresh_proposal()
    {
        var number = await CreateAndSendAsync(unitPrice: 500m);
        var actionId = await ProposeMarkPaidAsync(number);

        // Somebody with the authority to do it moves the invoice underneath the open approval.
        using var admin = await factory.ClientForAsync("ana@demo");
        (await admin.PostWriteAsync($"/api/invoices/{number}/cancel")).EnsureSuccessStatusCode();

        using var accountant = await factory.ClientForAsync("carlos@demo");
        using var approved = await accountant.PostAsync($"/api/actions/{actionId}/approve", content: null);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var outcome = await approved.Content.ReadFromJsonAsync<OutcomePayload>();

        Assert.Equal("approved", outcome!.DecisionStatus);
        Assert.Equal("failed", outcome.ExecutionStatus);
        Assert.Contains("changed after this was proposed", outcome.Message);

        var execution = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Failed, execution.Status);
        Assert.Equal("resource_changed", execution.ErrorCode);

        // The cancellation stands and nothing else happened to the invoice.
        Assert.Equal(InvoiceStatus.Cancelled, await StatusOfAsync(number));
    }

    /// <summary>
    /// The proposal records the revision it saw, and the command hash covers it — so an approval
    /// cannot be pointed at a different version of the same invoice by changing nothing at all.
    /// </summary>
    [Fact]
    public async Task A_proposal_captures_the_revision_it_was_made_against()
    {
        var number = await CreateAndSendAsync(unitPrice: 500m);
        var actionId = await ProposeMarkPaidAsync(number);

        var action = await ActionAsync(actionId);

        Assert.Equal(2, action.ExpectedResourceRevision);
        Assert.Equal(
            CommandHash.Of(action.ToolName, action.ArgsJson, 2),
            action.CommandHash);
    }

    /// <summary>
    /// A draft creation has no existing target, so there is nothing for anybody to have changed
    /// underneath it and no precondition to carry.
    /// </summary>
    [Fact]
    public async Task Creating_a_draft_carries_no_precondition()
    {
        factory.Model.Script(
            [
                StructuredCall("create_draft_invoice", new
                {
                    customer_name = "Delta Logística",
                    lines = new[] { new { description = "Draft with no target", quantity = 1, unit_price = 200m } },
                }),
            ],
            [new TextContent("Confirm?")]);

        using var client = await factory.ClientForAsync("carlos@demo");
        var events = await ChatEventsAsync(client, "create a draft for Delta");
        var actionId = Parse<ApprovalPayload>(events.Single(e => e.Name == "approval_required").Data).ActionId;

        var action = await ActionAsync(actionId);

        Assert.Null(action.ExpectedResourceRevision);
        Assert.Equal(CommandHash.Of(action.ToolName, action.ArgsJson, null), action.CommandHash);
    }

    /// <summary>
    /// Two writers on one invoice. The revision is an EF concurrency token, so the second update
    /// finds no row at its expected revision and raises rather than quietly winning.
    /// </summary>
    [Fact]
    public async Task Two_writers_on_one_invoice_do_not_silently_overwrite_each_other()
    {
        var number = await CreateAndSendAsync(unitPrice: 40m);

        using var first = await Loaded(number);
        using var second = await Loaded(number);

        first.Invoice.MarkPaid(factory.Clock.UtcNow);
        await first.Db.SaveChangesAsync();

        second.Invoice.Cancel();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.Db.SaveChangesAsync());

        Assert.Equal(InvoiceStatus.Paid, await StatusOfAsync(number));
    }

    // --- helpers ---------------------------------------------------------------------------------

    private async Task<string> CreateAndSendAsync(decimal unitPrice)
    {
        using var client = await factory.ClientForAsync("carlos@demo");

        using var created = await client.PostWriteAsync("/api/invoices", new
        {
            customerName = "Delta Logística",
            lines = new[] { new { description = "Precondition test", quantity = 1, unitPrice } },
        });
        created.EnsureSuccessStatusCode();

        var number = (await created.Content.ReadFromJsonAsync<InvoiceDetail>())!.Number;
        (await client.PostWriteAsync($"/api/invoices/{number}/send")).EnsureSuccessStatusCode();

        return number;
    }

    private async Task<Guid> ProposeMarkPaidAsync(string number)
    {
        factory.Model.Script([Call("mark_invoice_paid", new { number })], [new TextContent("Confirm?")]);

        using var client = await factory.ClientForAsync("carlos@demo");
        var events = await ChatEventsAsync(client, $"mark {number} as paid");

        return Parse<ApprovalPayload>(events.Single(e => e.Name == "approval_required").Data).ActionId;
    }

    private async Task<TrackedInvoice> Loaded(string number)
    {
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.SingleAsync(i => i.Number == number);

        return new TrackedInvoice(scope, db, invoice);
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

    private async Task<InvoiceStatus> StatusOfAsync(string number)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.Number == number)
            .Select(invoice => invoice.Status)
            .SingleAsync();
    }

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

    /// <summary>
    /// A scripted call whose arguments are not all strings. The plain helper flattens everything
    /// through <c>GetString()</c>, which is enough for the number-taking tools and turns an array of
    /// invoice lines into null.
    /// </summary>
    private static FunctionCallContent StructuredCall(string name, object arguments)
    {
        var json = JsonSerializer.SerializeToElement(arguments, JsonSerializerOptions.Web);

        return new FunctionCallContent(
            Guid.NewGuid().ToString("N"),
            name,
            json.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value));
    }

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!;

    /// <summary>An invoice loaded in its own scope, so two of them are genuinely two writers.</summary>
    private sealed record TrackedInvoice(IServiceScope Scope, AppDbContext Db, Invoice Invoice) : IDisposable
    {
        public void Dispose() => Scope.Dispose();
    }

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
