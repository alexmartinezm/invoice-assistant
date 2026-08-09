using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Domain;
using Api.Features.Invoices;
using Api.Infrastructure;
using Api.Infrastructure.Delivery;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>
/// The boundary this system cannot make transactional, and what it does about that.
/// </summary>
/// <remarks>
/// The test that justifies the whole design is
/// <see cref="A_provider_that_accepts_and_loses_the_response_reconciles_to_one_delivery"/>. Everything
/// else here is scaffolding for being able to write it: the invoice is Sent, the provider has the
/// message, nobody knows, and the recovery must not produce a second email.
/// </remarks>
public class OutboxDeliveryTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    [Fact]
    public async Task Sending_commits_the_transition_and_queues_the_delivery()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateDraftAsync();

        using var response = await client.PostWriteAsync($"/api/invoices/{number}/send");

        // 202, not 200: the ledger change is done, the email is not, and the status code says so.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var invoice = await response.Content.ReadFromJsonAsync<InvoiceDetail>();
        Assert.Equal("Sent", invoice!.Status);
        Assert.Equal("queued", invoice.Delivery!.Status);

        // Nothing has been sent, and nothing has been called: the provider is only reached from the
        // worker, never from inside the transaction.
        var delivery = await DeliveryForAsync(number);
        Assert.Equal(0, factory.Provider.RequestsFor(delivery.ProviderKey));
        Assert.DoesNotContain(delivery.ProviderKey, factory.Provider.Receipts.Keys);
    }

    [Fact]
    public async Task The_worker_delivers_a_queued_invoice_exactly_once()
    {
        var number = await SendAsync();

        await factory.RunOutboxAsync();

        var delivery = await DeliveryForAsync(number);
        Assert.Equal(InvoiceDeliveryStatus.Delivered, delivery.Status);
        Assert.NotNull(delivery.ProviderMessageId);
        Assert.Equal(1, factory.Provider.RequestsFor(delivery.ProviderKey));

        // A second pass asks nothing: the outbox row is completed, not merely quiet.
        await factory.RunOutboxAsync();
        Assert.Equal(1, factory.Provider.RequestsFor(delivery.ProviderKey));

        var message = await OutboxForAsync(delivery.Id);
        Assert.Equal(OutboxStatus.Completed, message.Status);
    }

    /// <summary>
    /// The demonstration. The provider takes the message and the answer is lost on the way back:
    /// one receipt exists on their side, our side knows nothing, and the recovery has to be a
    /// question rather than a resend.
    /// </summary>
    [Fact]
    public async Task A_provider_that_accepts_and_loses_the_response_reconciles_to_one_delivery()
    {
        var number = await SendAsync();

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterProviderAcceptedBeforeReceiptReturned);
        await factory.RunDispatchAsync();

        var afterLoss = await DeliveryForAsync(number);
        Assert.Equal(InvoiceDeliveryStatus.Unknown, afterLoss.Status);
        Assert.Null(afterLoss.SettledAt);

        // The provider does have it. That is what makes "it failed" the wrong answer.
        Assert.Contains(afterLoss.ProviderKey, factory.Provider.Receipts.Keys);

        await PassReconcileDelayAsync();
        await factory.RunReconcileAsync();

        var reconciled = await DeliveryForAsync(number);
        Assert.Equal(InvoiceDeliveryStatus.Delivered, reconciled.Status);
        Assert.Equal(
            factory.Provider.Receipts[reconciled.ProviderKey].ProviderMessageId,
            reconciled.ProviderMessageId);

        // Asked once, delivered once. Reconciling queried the provider; it did not send again.
        Assert.Equal(1, factory.Provider.RequestsFor(reconciled.ProviderKey));
    }

    /// <summary>
    /// An authoritative refusal. The outbox row completes rather than deferring — retrying would be
    /// refused identically, and a queue that keeps retrying a permanent failure is a queue that
    /// never drains.
    /// </summary>
    [Fact]
    public async Task A_deterministic_rejection_settles_as_failed_and_is_not_retried()
    {
        var number = await SendAsync(recipient: string.Empty);

        await factory.RunOutboxAsync();

        var delivery = await DeliveryForAsync(number);
        Assert.Equal(InvoiceDeliveryStatus.Failed, delivery.Status);
        Assert.Contains("no_recipient", delivery.LastError);
        Assert.Equal(1, delivery.Attempts);
        Assert.DoesNotContain(delivery.ProviderKey, factory.Provider.Receipts.Keys);

        var message = await OutboxForAsync(delivery.Id);
        Assert.Equal(OutboxStatus.Completed, message.Status);

        // And it stays failed: a second pass has nothing to pick up.
        await factory.RunOutboxAsync();
        Assert.Equal(1, (await DeliveryForAsync(number)).Attempts);
        Assert.Equal(1, factory.Provider.RequestsFor(delivery.ProviderKey));
    }

    /// <summary>
    /// The invoice is Sent and the customer has nothing. Both facts are true, and both are visible —
    /// which is the entire reason delivery is a separate record.
    /// </summary>
    [Fact]
    public async Task A_failed_delivery_does_not_make_the_invoice_look_undelivered_or_unsent()
    {
        var number = await SendAsync(recipient: string.Empty);
        await factory.RunOutboxAsync();

        using var client = await factory.ClientForAsync("carlos@demo");
        var invoice = await client.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{number}");

        Assert.Equal("Sent", invoice!.Status);
        Assert.Equal("failed", invoice.Delivery!.Status);
    }

    /// <summary>
    /// Two workers on one queue. <c>SKIP LOCKED</c> plus a lease makes that a throughput decision
    /// rather than a correctness one.
    /// </summary>
    [Fact]
    public async Task Two_workers_dispatch_each_message_once()
    {
        var numbers = new List<string>();
        for (var index = 0; index < 6; index++)
        {
            numbers.Add(await SendAsync());
        }

        await Task.WhenAll(factory.RunDispatchAsync(), factory.RunDispatchAsync());

        foreach (var number in numbers)
        {
            var delivery = await DeliveryForAsync(number);
            Assert.Equal(InvoiceDeliveryStatus.Delivered, delivery.Status);

            // One attempt each: no message was picked up by both workers.
            Assert.Equal(1, delivery.Attempts);
            Assert.Equal(1, factory.Provider.RequestsFor(delivery.ProviderKey));
        }
    }

    /// <summary>
    /// A worker that dies after leasing a row and before calling the provider. The lease is what
    /// lets the next one take over, and nothing was sent in the meantime.
    /// </summary>
    [Fact]
    public async Task A_worker_that_dies_before_calling_the_provider_leaves_the_work_recoverable()
    {
        var number = await SendAsync();

        var queued = await DeliveryForAsync(number);

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterOutboxClaimBeforeProviderCall);
        await Assert.ThrowsAsync<InjectedFaultException>(() => factory.RunDispatchAsync());

        Assert.Equal(0, factory.Provider.RequestsFor(queued.ProviderKey));
        Assert.Equal(InvoiceDeliveryStatus.Queued, (await DeliveryForAsync(number)).Status);

        // The lease has to lapse before another worker may take it — that is what stops two of them
        // calling the provider at once when the first is merely slow rather than dead.
        await ExpireLeasesAsync();
        await factory.RunOutboxAsync();

        Assert.Equal(InvoiceDeliveryStatus.Delivered, (await DeliveryForAsync(number)).Status);
        Assert.Equal(1, factory.Provider.RequestsFor(queued.ProviderKey));
    }

    /// <summary>
    /// The assistant path, end to end. An approved send does not report success: it reports that the
    /// delivery is being confirmed, and only becomes Succeeded when the provider says so.
    /// </summary>
    [Fact]
    public async Task An_approved_send_stays_executing_until_its_delivery_settles()
    {
        var number = await CreateDraftAsync();
        var actionId = await ProposeSendAsync(number);

        using var client = await factory.ClientForAsync("carlos@demo");
        using var approved = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);

        // 202 while the answer is still owed. A client treating 200 as "finished" is right to.
        Assert.Equal(HttpStatusCode.Accepted, approved.StatusCode);
        var outcome = await approved.Content.ReadFromJsonAsync<OutcomePayload>();
        Assert.Equal("approved", outcome!.DecisionStatus);
        Assert.Equal("executing", outcome.ExecutionStatus);

        var queued = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Executing, queued.Status);
        Assert.NotNull(queued.DeliveryId);

        await factory.RunOutboxAsync();

        var settled = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Succeeded, settled.Status);
        Assert.NotNull(settled.ProviderMessageId);

        // The conversation gets its closing line from whoever settled the execution — here, the
        // worker, minutes after the person clicked approve.
        var messages = await MessagesForAsync(actionId);
        Assert.Single(messages, message => message.StartsWith("Done:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same path with the answer lost. The user is told the outcome is unconfirmed — not that it
    /// worked, and not that it failed — and reconciliation later settles it without a second email.
    /// </summary>
    [Fact]
    public async Task An_approved_send_whose_answer_is_lost_says_so_and_then_reconciles()
    {
        var number = await CreateDraftAsync();
        var actionId = await ProposeSendAsync(number);

        using var client = await factory.ClientForAsync("carlos@demo");
        (await client.PostAsync($"/api/actions/{actionId}/approve", content: null)).Dispose();

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterProviderAcceptedBeforeReceiptReturned);
        await factory.RunDispatchAsync();

        var unknown = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Unknown, unknown.Status);
        Assert.Equal("delivery_unknown", unknown.ErrorCode);

        // The model's own line is there; a closing line is not. An unconfirmed outcome has nothing
        // to close with, and "Done" beside a delivery nobody has confirmed is the claim this whole
        // feature exists to stop making.
        Assert.DoesNotContain(
            await MessagesForAsync(actionId), message => message.StartsWith("Done:", StringComparison.Ordinal));

        await PassReconcileDelayAsync();
        await factory.RunReconcileAsync();

        var settled = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Succeeded, settled.Status);
        Assert.Equal(1, factory.Provider.RequestsFor((await DeliveryForAsync(number)).ProviderKey));
        Assert.Single(await MessagesForAsync(actionId), message => message.StartsWith("Done:", StringComparison.Ordinal));
    }

    // --- helpers ---------------------------------------------------------------------------------

    private async Task<string> CreateDraftAsync(string? recipient = null)
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var customer = await CustomerAsync(recipient);

        using var created = await client.PostWriteAsync("/api/invoices", new
        {
            customerName = customer,
            lines = new[] { new { description = "Delivery test", quantity = 1, unitPrice = 40m } },
        });
        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<InvoiceDetail>())!.Number;
    }

    private async Task<string> SendAsync(string? recipient = null)
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateDraftAsync(recipient);

        (await client.PostWriteAsync($"/api/invoices/{number}/send")).EnsureSuccessStatusCode();
        return number;
    }

    /// <summary>
    /// A customer to invoice. Passing an empty recipient creates one with no deliverable address,
    /// which is how the deterministic-rejection path is reached without a switch in the provider.
    /// </summary>
    private async Task<string> CustomerAsync(string? recipient)
    {
        if (recipient is null)
        {
            return "Delta Logística";
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var name = $"Unreachable {Guid.CreateVersion7():N}"[..24];
        db.Customers.Add(new Customer { Name = name, TaxId = "B00000000", Email = recipient });
        await db.SaveChangesAsync();

        return name;
    }

    private async Task<Guid> ProposeSendAsync(string number)
    {
        factory.Model.Script([Call("send_invoice", new { number })], [new TextContent("Confirm?")]);

        using var client = await factory.ClientForAsync("carlos@demo");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message = $"send {number}" }),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var events = await ServerSentEvents.ReadAllAsync(response);
        return Parse<ApprovalPayload>(events.Single(e => e.Name == "approval_required").Data).ActionId;
    }

    private async Task<InvoiceDelivery> DeliveryForAsync(string number)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.InvoiceDeliveries.AsNoTracking().SingleAsync(d => d.InvoiceNumber == number);
    }

    private async Task<OutboxMessage> OutboxForAsync(Guid deliveryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.DeliveryId == deliveryId);
    }

    private async Task<ActionExecution> ExecutionAsync(Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.ActionExecutions.AsNoTracking().SingleAsync(e => e.PendingActionId == actionId);
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
            .Where(m => m.ConversationId == conversationId && m.Role == MessageRole.Assistant)
            .Select(m => m.Content)
            .ToListAsync();
    }

    private async Task ExpireLeasesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Reaches past the entity on purpose: a lease lapses through the passage of time, and the
        // frozen test clock has none.
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE outbox_messages SET "LeaseExpiresAt" = {factory.Clock.UtcNow.AddMinutes(-1)} WHERE "Status" = 'Pending'""");
    }

    /// <summary>
    /// Fast-forwards a parked row past <see cref="DeliveryReconciler.ReconcileDelay"/>, the same way
    /// <see cref="ExpireLeasesAsync"/> fast-forwards a lease: by writing the column directly, because
    /// the frozen test clock has no passage of time for the reconciler's own <c>AvailableAt</c> gate
    /// to wait out.
    /// </summary>
    private async Task PassReconcileDelayAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE outbox_messages SET "AvailableAt" = {factory.Clock.UtcNow}
            WHERE "Status" = 'AwaitingReconciliation'
            """);
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

    private sealed record ApprovalPayload(Guid ActionId, string Tool, string Summary);

    private sealed record OutcomePayload(
        Guid ActionId,
        Guid? ExecutionId,
        string DecisionStatus,
        string? ExecutionStatus,
        string Summary,
        string Message);
}
