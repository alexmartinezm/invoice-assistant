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
/// What happens on the paths nobody is watching.
/// </summary>
/// <remarks>
/// <para>
/// The happy path had tests from the start. These are the recovery paths: the request that never
/// came back, the receipt read after a restart, the provider that cannot be asked what it has. Each
/// one starts from a state the system can genuinely reach and asks the only question that matters —
/// how many times did the customer's money move, and how many emails did they get?
/// </para>
/// <para>
/// Nothing here is proved with a sleep. Leases lapse by writing the clock back, the outbox is driven
/// by hand, and the faults are named checkpoints rather than timing.
/// </para>
/// </remarks>
public class DurableRecoveryTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    /// <summary>
    /// A process that dies between claiming the attempt and putting the request on the wire. The
    /// execution is durable and nothing has happened yet, so the retry has to be the first write —
    /// and exactly one write.
    /// </summary>
    /// <remarks>
    /// Before the lease existed this was the unrecoverable state: <c>Executing</c> was reachable and
    /// nothing could ever leave it, because the claim only accepted <c>Pending</c> or <c>Unknown</c>
    /// and no background pass looked at it.
    /// </remarks>
    [Fact]
    public async Task An_attempt_abandoned_before_the_request_went_out_is_resumed_exactly_once()
    {
        var number = await CreateAndSendAsync();
        var actionId = await ProposeAsync("mark_invoice_paid", number);

        using var client = await factory.ClientForAsync("carlos@demo");

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterApprovalClaimCommitted);
        using var crashed = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);
        Assert.Equal(HttpStatusCode.InternalServerError, crashed.StatusCode);

        var abandoned = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Executing, abandoned.Status);
        Assert.NotNull(abandoned.AttemptExpiresAt);
        Assert.Equal(1, abandoned.AttemptCount);
        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));

        // While the lease is live the attempt belongs to somebody. A retry is told where it has got
        // to rather than being allowed to race a request that may still be in flight.
        using var tooSoon = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);
        Assert.Equal(HttpStatusCode.Accepted, tooSoon.StatusCode);
        Assert.Equal(1, (await ExecutionAsync(actionId)).AttemptCount);
        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));

        await ExpireAttemptLeasesAsync();

        using var resumed = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);

        var settled = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Succeeded, settled.Status);
        Assert.Null(settled.AttemptExpiresAt);
        Assert.Equal(2, settled.AttemptCount);

        // Two attempts, one execution, one paid invoice. The attempt chain is a record of what was
        // tried; the invoice is the record of what happened, and it moved once.
        Assert.Equal(1, await ExecutionCountAsync(actionId));
        Assert.Equal(InvoiceStatus.Paid, await StatusOfAsync(number));
        Assert.Single(await MessagesForAsync(actionId), m => m.StartsWith("Done:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same abandonment, with nobody retrying. The reconciler finds no evidence either way, so it
    /// releases the attempt and says the outcome is unknown — it does not re-run the write.
    /// </summary>
    /// <remarks>
    /// This is the boundary the reconciler must never cross. It holds no bearer token and cannot
    /// acquire one, so re-executing somebody's write would mean synthesising an identity for it.
    /// Leaving the row resumable is the honest outcome; inventing one is not.
    /// </remarks>
    [Fact]
    public async Task The_reconciler_releases_an_abandoned_attempt_without_re_executing_it()
    {
        var number = await CreateAndSendAsync();
        var actionId = await ProposeAsync("mark_invoice_paid", number);

        using var client = await factory.ClientForAsync("carlos@demo");

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterApprovalClaimCommitted);
        (await client.PostAsync($"/api/actions/{actionId}/approve", content: null)).Dispose();

        // A live lease is somebody else's business, so the reconciler leaves it alone.
        Assert.Equal(0, await factory.RunExecutionReconcileAsync());

        await ExpireAttemptLeasesAsync();
        Assert.Equal(1, await factory.RunExecutionReconcileAsync());

        var released = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Unknown, released.Status);
        Assert.Equal("attempt_abandoned", released.ErrorCode);
        Assert.Equal(1, released.AttemptCount);

        // Released, not run: the reconciler has no identity to run it under, and the invoice proves
        // it did not find one.
        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));
        Assert.DoesNotContain(
            await MessagesForAsync(actionId), m => m.StartsWith("Done:", StringComparison.Ordinal));

        // And the caller can still finish it, under their own identity, on the same key.
        using var resumed = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(InvoiceStatus.Paid, await StatusOfAsync(number));
    }

    /// <summary>
    /// A send whose answer was lost after the local transaction committed. The receipt says
    /// <c>202</c> with a delivery id, and replaying it has to mean what hearing it would have meant:
    /// the invoice is issued and the outbox owns the rest.
    /// </summary>
    /// <remarks>
    /// The receipt path used to read any <c>2xx</c> as success. That turned a queued email into a
    /// settled "Done" the outbox could not correct, because a settled execution is terminal — the
    /// one bug where the user is told the customer has been emailed and nobody has been.
    /// </remarks>
    [Fact]
    public async Task A_lost_send_response_replays_as_awaiting_delivery_rather_than_as_done()
    {
        var number = await CreateDraftAsync();
        var actionId = await ProposeAsync("send_invoice", number);

        using var client = await factory.ClientForAsync("carlos@demo");

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterBusinessTransactionCommitBeforeResponse);
        (await client.PostAsync($"/api/actions/{actionId}/approve", content: null)).Dispose();

        var lost = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Unknown, lost.Status);

        // The local write did commit — that is what makes this dangerous rather than merely failed.
        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));

        using var retry = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);

        var reconciled = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Executing, reconciled.Status);
        Assert.NotNull(reconciled.DeliveryId);
        Assert.Equal((await DeliveryForAsync(number)).Id, reconciled.DeliveryId);

        // Not settled, so no closing line, so nothing has claimed the customer was emailed.
        Assert.DoesNotContain(
            await MessagesForAsync(actionId), m => m.StartsWith("Done:", StringComparison.Ordinal));

        // The execution is the outbox's now, and it stays that way however many times somebody
        // clicks: the delivery id does not change and no second request goes out.
        using var again = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(reconciled.DeliveryId, (await ExecutionAsync(actionId)).DeliveryId);

        await factory.RunOutboxAsync();

        var delivery = await DeliveryForAsync(number);
        Assert.Equal(InvoiceDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal(ActionExecutionStatus.Succeeded, (await ExecutionAsync(actionId)).Status);
        Assert.Equal(1, factory.Provider.MessagesFor(delivery.ProviderKey));
        Assert.Single(await MessagesForAsync(actionId), m => m.StartsWith("Done:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A provider that neither deduplicates nor can be asked what it has. There is no safe recovery
    /// from an ambiguous answer, so the row is parked for a person and the customer gets one email.
    /// </summary>
    /// <remarks>
    /// The spec's rule, and the one the dispatcher used to break: it deferred every ambiguous send
    /// back to <c>Pending</c>, which the passage of time then turned into a resend. That was only
    /// safe because the demo provider happened to deduplicate — a property of the stand-in, not of
    /// the design.
    /// </remarks>
    [Fact]
    public async Task A_provider_that_cannot_be_asked_parks_an_ambiguous_delivery_instead_of_resending()
    {
        await DrainOutboxAsync();
        using var capabilities = factory.ProviderThat(honoursStableKey: false, supportsReceiptLookup: false);

        var number = await SendAsync();
        var providerKey = (await DeliveryForAsync(number)).ProviderKey;

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterProviderAcceptedBeforeReceiptReturned);
        await factory.RunDispatchAsync();

        var ambiguous = await DeliveryForAsync(number);
        Assert.Equal(InvoiceDeliveryStatus.Unknown, ambiguous.Status);
        Assert.Null(ambiguous.SettledAt);

        var parked = await OutboxForAsync(ambiguous.Id);
        Assert.Equal(OutboxStatus.AwaitingReconciliation, parked.Status);

        // Three full passes. A parked row is out of the dispatcher's Pending predicate and there is
        // nothing to ask, so neither half of the worker has anything it may do.
        for (var pass = 0; pass < 3; pass++)
        {
            await factory.RunOutboxAsync();
        }

        Assert.Equal(OutboxStatus.AwaitingReconciliation, (await OutboxForAsync(ambiguous.Id)).Status);
        Assert.Equal(InvoiceDeliveryStatus.Unknown, (await DeliveryForAsync(number)).Status);

        // One request, one message. Waiting for a person is the correct answer here, and it is the
        // only one that does not risk a second email.
        Assert.Equal(1, factory.Provider.RequestsFor(providerKey));
        Assert.Equal(1, factory.Provider.MessagesFor(providerKey));
    }

    /// <summary>
    /// A provider that does not deduplicate but can be asked. The reconciler asks, and the answer is
    /// what settles the delivery — no resend, against a provider where a resend would be a duplicate.
    /// </summary>
    [Fact]
    public async Task A_lookup_only_provider_is_asked_before_anything_is_sent_again()
    {
        await DrainOutboxAsync();
        using var capabilities = factory.ProviderThat(honoursStableKey: false, supportsReceiptLookup: true);

        var number = await SendAsync();
        var providerKey = (await DeliveryForAsync(number)).ProviderKey;

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterProviderAcceptedBeforeReceiptReturned);
        await factory.RunDispatchAsync();

        Assert.Equal(InvoiceDeliveryStatus.Unknown, (await DeliveryForAsync(number)).Status);

        await factory.RunOutboxAsync();

        var settled = await DeliveryForAsync(number);
        Assert.Equal(InvoiceDeliveryStatus.Delivered, settled.Status);
        Assert.Equal(factory.Provider.Receipts[providerKey].ProviderMessageId, settled.ProviderMessageId);
        Assert.Equal(OutboxStatus.Completed, (await OutboxForAsync(settled.Id)).Status);

        // Asked once, sent once. Against this provider a resend would have produced a second email,
        // which is exactly what the lookup is for.
        Assert.Equal(1, factory.Provider.RequestsFor(providerKey));
        Assert.Equal(1, factory.Provider.MessagesFor(providerKey));
    }

    /// <summary>
    /// The other answer a lookup can give. "I never received it" is authoritative in the one
    /// direction that makes sending again correct rather than duplicating, so the row goes back to
    /// the queue.
    /// </summary>
    [Fact]
    public async Task A_delivery_the_provider_never_received_goes_back_to_the_queue()
    {
        await DrainOutboxAsync();

        var number = await SendAsync();
        var delivery = await DeliveryForAsync(number);

        // The state a process that died mid-dispatch leaves behind: we recorded that we do not know,
        // and the provider was never actually reached. Written directly because no fault checkpoint
        // sits between "leased" and "the provider has it" — that is the gap being tested.
        await ParkAsUnknownAsync(delivery.Id);

        Assert.Equal(0, factory.Provider.RequestsFor(delivery.ProviderKey));

        await factory.RunReconcileAsync();

        Assert.Equal(InvoiceDeliveryStatus.Queued, (await DeliveryForAsync(number)).Status);
        Assert.Equal(OutboxStatus.Pending, (await OutboxForAsync(delivery.Id)).Status);

        await factory.RunOutboxAsync();

        Assert.Equal(InvoiceDeliveryStatus.Delivered, (await DeliveryForAsync(number)).Status);
        Assert.Equal(1, factory.Provider.MessagesFor(delivery.ProviderKey));
    }

    /// <summary>
    /// Somebody else's execution id, used as an idempotency key on a write of one's own. It must link
    /// no execution: a UUID a user happens to know is not authorization to settle anything.
    /// </summary>
    /// <remarks>
    /// The Admin's approval id is visible to the Accountant who proposed it, so this is a key the
    /// attacker genuinely has. The match used to be on the id alone, which meant the Accountant's
    /// unrelated send would settle the Admin's execution and write a line into the Admin's
    /// conversation saying their action was done.
    /// </remarks>
    [Fact]
    public async Task Another_users_execution_id_as_an_idempotency_key_links_no_execution()
    {
        var theirs = await CreateDraftAsync();
        var actionId = await ProposeAsync("send_invoice", theirs);

        // The Admin approves the Accountant's proposal, so the execution belongs to the Admin.
        using var admin = await factory.ClientForAsync("ana@demo");
        using var approved = await admin.PostAsync($"/api/actions/{actionId}/approve", content: null);
        approved.EnsureSuccessStatusCode();

        var executionId = (await ExecutionAsync(actionId)).Id;

        using var accountant = await factory.ClientForAsync("carlos@demo");
        var mine = await CreateDraftAsync();

        using var sent = await accountant.PostWriteAsync(
            $"/api/invoices/{mine}/send", key: executionId.ToString());
        sent.EnsureSuccessStatusCode();

        // The Accountant's own delivery went out, bound to nothing.
        Assert.Null((await DeliveryForAsync(mine)).ExecutionId);

        // And the Admin's execution is still waiting on its own delivery, untouched.
        var untouched = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Executing, untouched.Status);
        Assert.Equal((await DeliveryForAsync(theirs)).Id, untouched.DeliveryId);
    }

    /// <summary>
    /// A provider that refuses. The invoice is issued and the customer has nothing — both true, and
    /// the sentence has to say both.
    /// </summary>
    /// <remarks>
    /// "Nothing changed" is the general failure copy and it is a lie here: the ledger moved, the
    /// customer owes money, and only the email did not happen. A user told nothing changed would go
    /// looking for a draft that is not there.
    /// </remarks>
    [Fact]
    public async Task A_refused_delivery_says_the_invoice_was_issued_rather_than_that_nothing_changed()
    {
        var number = await CreateDraftAsync(recipient: string.Empty);
        var actionId = await ProposeAsync("send_invoice", number);

        using var client = await factory.ClientForAsync("carlos@demo");
        (await client.PostAsync($"/api/actions/{actionId}/approve", content: null)).Dispose();

        await factory.RunOutboxAsync();

        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));
        Assert.Equal(InvoiceDeliveryStatus.Failed, (await DeliveryForAsync(number)).Status);

        var execution = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Failed, execution.Status);
        Assert.Equal("delivery_rejected", execution.ErrorCode);

        var closing = Assert.Single(
            await MessagesForAsync(actionId), m => m.Contains("would not deliver", StringComparison.Ordinal));
        Assert.Contains("the invoice was issued", closing, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing changed", closing, StringComparison.Ordinal);

        // And the client is handed that sentence rather than left to derive one from "failed".
        var view = await client.GetFromJsonAsync<ExecutionPayload>($"/api/action-executions/{execution.Id}");
        Assert.Equal(closing, view!.Message);
    }

    /// <summary>
    /// A proposal edited between being shown and being approved. The fingerprint no longer describes
    /// the command, so the approval buys nothing and the write does not happen.
    /// </summary>
    /// <remarks>
    /// The hash was already stored and already copied onto the execution; nothing recomputed it. A
    /// row rewritten by any path that can reach the table — a second service, a migration, an
    /// operator — would have executed under a person's approval of a different sentence.
    /// </remarks>
    [Fact]
    public async Task A_proposal_whose_arguments_were_edited_is_refused_and_executes_nothing()
    {
        var proposed = await CreateAndSendAsync();
        var substituted = await CreateAndSendAsync();

        var actionId = await ProposeAsync("mark_invoice_paid", proposed);
        await RewriteArgumentsAsync(actionId, substituted);

        using var client = await factory.ClientForAsync("carlos@demo");
        using var refused = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("action_tampered", problem!.Code);

        // Neither invoice moved, and there is no execution to explain one having moved.
        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(proposed));
        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(substituted));
        Assert.Equal(0, await ExecutionCountAsync(actionId));

        var action = await ActionAsync(actionId);
        Assert.Equal(PendingActionStatus.Rejected, action.Status);
        Assert.Equal(ActionResolutionReason.PolicyDenied, action.ResolutionReason);
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// A draft. The default price is above the policy's confirmation threshold, because a proposal
    /// is what most of these tests need and a cheap invoice is auto-allowed instead.
    /// </summary>
    private async Task<string> CreateDraftAsync(string? recipient = null, decimal unitPrice = 500m)
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var customer = await CustomerAsync(recipient);

        using var created = await client.PostWriteAsync("/api/invoices", new
        {
            customerName = customer,
            lines = new[] { new { description = "Recovery test", quantity = 1, unitPrice } },
        });
        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<InvoiceDetail>())!.Number;
    }

    private async Task<string> CreateAndSendAsync()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateDraftAsync();

        (await client.PostWriteAsync($"/api/invoices/{number}/send")).EnsureSuccessStatusCode();
        return number;
    }

    /// <summary>Sends an invoice through the API, leaving its delivery queued for the outbox.</summary>
    private Task<string> SendAsync() => CreateAndSendAsync();

    /// <summary>
    /// Dispatches whatever earlier tests left queued.
    /// </summary>
    /// <remarks>
    /// The factory is shared across the class and the outbox is not per-test, so a fault armed for
    /// one delivery would otherwise be free to fire on somebody else's row in the same batch — a
    /// test that passes or fails depending on what ran before it.
    /// </remarks>
    private async Task DrainOutboxAsync()
    {
        for (var pass = 0; pass < 5 && await QueuedCountAsync() > 0; pass++)
        {
            await factory.RunDispatchAsync();
        }

        Assert.Equal(0, await QueuedCountAsync());
    }

    private async Task<int> QueuedCountAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.OutboxMessages.AsNoTracking().CountAsync(m => m.Status == OutboxStatus.Pending);
    }

    /// <summary>
    /// A customer to invoice. An empty recipient creates one with no deliverable address, which is
    /// how the deterministic-rejection path is reached without a switch inside the provider.
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

    private async Task<Guid> ProposeAsync(string tool, string number)
    {
        factory.Model.Script([Call(tool, new { number })], [new TextContent("Confirm?")]);

        using var client = await factory.ClientForAsync("carlos@demo");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message = $"{tool} {number}" }),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var events = await ServerSentEvents.ReadAllAsync(response);
        return Parse<ApprovalPayload>(events.Single(e => e.Name == "approval_required").Data).ActionId;
    }

    /// <summary>
    /// Swaps the frozen arguments for a different invoice, leaving the stored fingerprint describing
    /// the command the user was actually shown.
    /// </summary>
    private async Task RewriteArgumentsAsync(Guid actionId, string number)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tampered = JsonSerializer.Serialize(new { number }, JsonSerializerOptions.Web);

        await db.Database.ExecuteSqlAsync(
            $"""UPDATE pending_actions SET "ArgsJson" = {tampered}::json WHERE "Id" = {actionId}""");
    }

    /// <summary>
    /// Puts a queued delivery into the state a dispatcher that died mid-call leaves behind: we do
    /// not know, and the outbox row is parked waiting to be asked about.
    /// </summary>
    private async Task ParkAsUnknownAsync(Guid deliveryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlAsync(
            $"""UPDATE invoice_deliveries SET "Status" = 'Unknown' WHERE "Id" = {deliveryId}""");

        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE outbox_messages
            SET "Status" = 'AwaitingReconciliation', "LeaseOwner" = NULL, "LeaseExpiresAt" = NULL
            WHERE "DeliveryId" = {deliveryId}
            """);
    }

    /// <summary>
    /// Lapses every attempt lease. Reaches past the entity on purpose: a lease expires through the
    /// passage of time, and the frozen test clock has none.
    /// </summary>
    private async Task ExpireAttemptLeasesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE action_executions
            SET "AttemptExpiresAt" = {factory.Clock.UtcNow.AddMinutes(-1)}
            WHERE "AttemptExpiresAt" IS NOT NULL
            """);
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

    private async Task<InvoiceStatus> StatusOfAsync(string number)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.Number == number)
            .Select(invoice => invoice.Status)
            .SingleAsync();
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

    private sealed record ProblemPayload(string? Code, string? Detail);

    private sealed record ExecutionPayload(Guid ExecutionId, string Status, string? ErrorCode, string? Message);
}
