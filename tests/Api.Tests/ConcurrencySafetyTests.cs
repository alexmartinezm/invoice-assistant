using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Assistant.Tools;
using Api.Domain;
using Api.Features.Invoices;
using Api.Infrastructure;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests;

/// <summary>
/// A second, independent read of PR #16: races the first pass's own fixes did not close.
/// </summary>
/// <remarks>
/// Where <see cref="DurableRecoveryTests"/> proves the happy-path windows the feature promised to
/// close, this proves the ones a re-audit found still open — a transient answer settled as
/// terminal, a starved reconciler queue, two writers racing one row with nothing to stop the second
/// from landing on top of the first, and three smaller contract breaks.
/// </remarks>
public class ConcurrencySafetyTests(DeliveryApiFactory factory) : IClassFixture<DeliveryApiFactory>
{
    /// <summary>
    /// The idempotency filter's own "somebody else has this key" answer is not a refusal — it is a
    /// twin still running. Settling the execution as <c>Failed</c> for it would be a false negative
    /// on a write that may yet land.
    /// </summary>
    [Fact]
    public async Task A_request_in_progress_response_reconciles_as_unknown_not_failed()
    {
        var number = await CreateAndSendAsync();
        var actionId = await ProposeAsync("mark_invoice_paid", number);

        using var client = await factory.ClientForAsync("carlos@demo");

        // The execution's id is minted inside the claim and cannot be known before it — so the
        // first approve is stopped right after the claim commits, which is durable and gives this
        // test a real id to plant a twin's claim under before anything is ever sent.
        factory.Faults.ThrowOnce(FaultCheckpoint.AfterApprovalClaimCommitted);
        (await client.PostAsync($"/api/actions/{actionId}/approve", content: null)).Dispose();

        var executionId = await ExecutionIdForAsync(actionId);
        await ExpireAttemptLeaseAsync(executionId);

        // Simulates a twin holding the same idempotency key: claimed, not yet settled. The
        // execution's own key doubles as the Idempotency-Key its send carries, so this is exactly
        // the row TransactionalIdempotencyFilter finds when this retry reaches the API.
        await ClaimKeyWithoutCompletingAsync(executionId, "carlos@demo", $"POST /api/invoices/{number}/mark-paid");

        using var response = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);

        // 202: the outcome is not settled, and above all is not a refusal. The old classifier read
        // every non-2xx under 500 as a deterministic failure, including this one.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var execution = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Unknown, execution.Status);
        Assert.NotEqual(ActionExecutionStatus.Failed, execution.Status);
        Assert.NotNull(execution.NextAttemptAt);

        // Nothing was decided against the invoice — the whole point of not treating "in progress"
        // as "refused".
        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));
    }

    /// <summary>
    /// An <c>Unknown</c> the reconciler could not settle must not be reselected on every single
    /// pass — that starves every other row behind it in the batch, forever, on the strength of one
    /// execution nothing will ever produce evidence for.
    /// </summary>
    [Fact]
    public async Task An_unknown_execution_with_no_evidence_is_deferred_not_reselected()
    {
        var number = await CreateAndSendAsync();
        var actionId = await ProposeAsync("mark_invoice_paid", number);

        using var client = await factory.ClientForAsync("carlos@demo");

        // Stopped before anything was ever sent, so no idempotency receipt exists under this
        // execution's key — genuinely nothing for a reconcile pass to find, ever, which is what a
        // transport failure before the server saw the request looks like from here.
        factory.Faults.ThrowOnce(FaultCheckpoint.AfterApprovalClaimCommitted);
        (await client.PostAsync($"/api/actions/{actionId}/approve", content: null)).Dispose();

        var executionId = await ExecutionIdForAsync(actionId);
        await ParkExecutionAsUnknownAsync(executionId);

        var firstPass = await factory.RunExecutionReconcileAsync();
        Assert.Equal(0, firstPass);

        var afterFirstPass = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Unknown, afterFirstPass.Status);

        // Deferred into the future relative to the (frozen) clock this pass ran under — the fact
        // that makes it fall out of the reconciler's own "what is due" query.
        Assert.True(afterFirstPass.NextAttemptAt > factory.Clock.UtcNow);

        // A second pass, no time having passed: the old code reselected this execution every time
        // because NextAttemptAt never moved. Now it is excluded, so the pass finds nothing.
        Assert.Equal(0, await factory.RunExecutionReconcileAsync());
    }

    /// <summary>
    /// The delivery reconciler must not ask the provider before the delay it was parked for has
    /// elapsed — asking too early is how a receipt the provider has not finished writing gets
    /// misread as "never received", and resent.
    /// </summary>
    [Fact]
    public async Task The_delivery_reconciler_does_not_ask_before_its_own_delay_has_passed()
    {
        var number = await SendAsync();
        var providerKey = (await DeliveryForAsync(number)).ProviderKey;

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterProviderAcceptedBeforeReceiptReturned);
        await factory.RunDispatchAsync();

        Assert.Equal(InvoiceDeliveryStatus.Unknown, (await DeliveryForAsync(number)).Status);

        // The one send above is what produced the ambiguity; RequestsFor counts sends, not
        // lookups, so this is the baseline a resend would move and a lookup would not.
        Assert.Equal(1, factory.Provider.RequestsFor(providerKey));

        // Reconciling immediately, with no time passed and AvailableAt still in the future: the
        // provider must not be asked yet.
        await factory.RunReconcileAsync();

        Assert.Equal(InvoiceDeliveryStatus.Unknown, (await DeliveryForAsync(number)).Status);

        // Unchanged: no resend happened. If the missing AvailableAt check let the reconciler lease
        // this row early, a lookup would find the receipt the provider already holds and settle the
        // delivery to Delivered — this asserts neither happened.
        Assert.Equal(1, factory.Provider.RequestsFor(providerKey));
    }

    /// <summary>
    /// Two concurrent settlers reconciling the same execution from the same stale snapshot: exactly
    /// one may write, and the loser must discover that rather than silently overwrite whatever the
    /// winner already committed.
    /// </summary>
    /// <remarks>
    /// This is the shape of the race the audit described — a live retry reconciling from a receipt
    /// while the outbox is settling the same execution's delivery — reduced to its essential
    /// mechanism: two writers, one row, one stale read shared by both. If the row can be
    /// overwritten by a second writer holding an older snapshot, it can be overwritten regardless of
    /// which two code paths produced the two writers.
    /// </remarks>
    [Fact]
    public async Task Two_concurrent_reconcilers_racing_one_execution_do_not_both_win()
    {
        var number = await CreateDraftAsync();
        var actionId = await ProposeAsync("send_invoice", number);

        using var client = await factory.ClientForAsync("carlos@demo");

        // The local transaction commits — invoice Sent, delivery Queued, a receipt with a 202 and a
        // delivery id — and the caller's own response is lost. The execution is Unknown with a
        // receipt sitting behind it, which is exactly the state two independent settlers can now
        // both reach for.
        factory.Faults.ThrowOnce(FaultCheckpoint.AfterBusinessTransactionCommitBeforeResponse);
        (await client.PostAsync($"/api/actions/{actionId}/approve", content: null)).Dispose();

        Assert.Equal(ActionExecutionStatus.Unknown, (await ExecutionAsync(actionId)).Status);

        var executionId = (await ExecutionAsync(actionId)).Id;

        // Both scopes load the row before either writes, so both carry the same original row
        // version — the race is decided by the database, not by which Task the scheduler runs
        // first.
        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();

        var (executorA, executionA) = await ReconcilerForAsync(scopeA, executionId);
        var (executorB, executionB) = await ReconcilerForAsync(scopeB, executionId);

        var results = await Task.WhenAll(
            executorA.TryReconcileFromReceiptAsync(executionA, CancellationToken.None),
            executorB.TryReconcileFromReceiptAsync(executionB, CancellationToken.None));

        // Deterministic, not probabilistic: with both starting from the same row version, the
        // database can settle the race only one way. Before the concurrency token existed, both
        // attach-as-Modified saves would have succeeded — this is precisely the assertion that
        // would have failed against the un-fixed code.
        Assert.Equal(1, results.Count(won => won));

        var settled = await ExecutionAsync(actionId);
        Assert.Equal(ActionExecutionStatus.Executing, settled.Status);
        Assert.NotNull(settled.DeliveryId);
        Assert.Equal((await DeliveryForAsync(number)).Id, settled.DeliveryId);
    }

    /// <summary>
    /// A header the caller sent and got wrong must not silently behave like no header at all —
    /// that would run an unconditional write exactly where the caller asked for a conditional one.
    /// </summary>
    [Fact]
    public async Task A_malformed_If_Match_is_refused_rather_than_treated_as_absent()
    {
        var number = await CreateAndSendAsync();

        using var client = await factory.ClientForAsync("carlos@demo");

        // Quoted, so it is a syntactically valid ETag/If-Match value and .NET's own header
        // validation lets it out the door — the point is that the *server's* parse of what is
        // inside the quotes fails, not that the header is malformed HTTP.
        using var response = await client.PatchWriteAsync(
            $"/api/invoices/{number}/due-date",
            new { dueDate = "2026-12-31" },
            ifMatch: "\"abc\"");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("invalid_precondition", problem!.Code);

        // Refused before it touched anything.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dueDate = await db.Invoices.AsNoTracking()
            .Where(i => i.Number == number).Select(i => i.DueDate).SingleAsync();
        Assert.NotEqual(new DateOnly(2026, 12, 31), dueDate);
    }

    /// <summary>
    /// A genuine concurrency race — two writes to the same invoice landing close enough together
    /// that the second's optimistic check fails at save time, not at the door — answers the same
    /// <c>412 resource_changed</c> an If-Match mismatch does, not an unexplained 500.
    /// </summary>
    /// <remarks>
    /// Fifteen concurrent writers rather than two, for the same reason
    /// <see cref="DurableActionTests.Twenty_concurrent_approvals_produce_one_decision_and_one_execution"/>
    /// uses twenty: two requests dispatched together do not reliably overlap between their own read
    /// and their own write, and a test that only sometimes exercises the race is a test that only
    /// sometimes means anything. Enough concurrent writers makes at least one genuine overlap a near
    /// certainty without asserting anything about exactly how many.
    /// </remarks>
    [Fact]
    public async Task A_concurrency_race_discovered_at_save_time_answers_412_not_500()
    {
        const int writers = 15;
        var number = await CreateAndSendAsync();

        using var client = await factory.ClientForAsync("carlos@demo");

        var responses = await Task.WhenAll(Enumerable.Range(0, writers).Select(
            index => client.PatchWriteAsync(
                $"/api/invoices/{number}/due-date", new { dueDate = $"2026-{10 + index % 2:00}-01" })));

        try
        {
            // At least one wins outright; at least one loses a race that is only reachable by more
            // than one writer landing close together, and none may answer 500 — before this fix an
            // unhandled DbUpdateConcurrencyException did exactly that for the loser, turning "this
            // changed under you" into an unexplained server error instead of the 412 the explicit
            // If-Match check already has a sentence for.
            Assert.Contains(responses, r => r.StatusCode is HttpStatusCode.OK);
            Assert.Contains(responses, r => r.StatusCode is HttpStatusCode.PreconditionFailed);
            Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);

            foreach (var failed in responses.Where(r => r.StatusCode is HttpStatusCode.PreconditionFailed))
            {
                var problem = await failed.Content.ReadFromJsonAsync<ProblemPayload>();
                Assert.Equal("resource_changed", problem!.Code);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// The foreign key is the backstop for the invariant the code already maintains by construction:
    /// an outbox row's delivery exists before the row pointing at it does. Proving the constraint is
    /// there is proving that invariant can no longer silently break.
    /// </summary>
    [Fact]
    public async Task An_outbox_row_cannot_reference_a_delivery_that_does_not_exist()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.OutboxMessages.Add(OutboxMessage.ForDelivery(
            new InvoiceDelivery
            {
                Id = Guid.CreateVersion7(),
                InvoiceId = Guid.CreateVersion7(),
                InvoiceNumber = "0000-0000",
                ProviderKey = $"orphan-{Guid.CreateVersion7():N}",
                Recipient = "nobody@example.com",
                CreatedAt = factory.Clock.UtcNow,
            },
            "{}",
            factory.Clock.UtcNow));

        // The delivery above was never added — only referenced. The foreign key on DeliveryId is
        // what turns that into a rejected write instead of a row nothing can ever resolve.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// A replayed response is supposed to be indistinguishable from the first — that is the entire
    /// idempotency contract — and a caller that created a resource still needs <c>Location</c> on
    /// the retry that lost the original reply.
    /// </summary>
    [Fact]
    public async Task A_replayed_response_carries_the_same_Location_and_ETag_as_the_original()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var key = Guid.CreateVersion7().ToString();
        var body = new
        {
            customerName = "Delta Logística",
            lines = new[] { new { description = "Header replay test", quantity = 1, unitPrice = 10m } },
        };

        using var first = await client.PostWriteAsync("/api/invoices", body, key);
        using var second = await client.PostWriteAsync("/api/invoices", body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.NotNull(second.Headers.Location);
        Assert.Equal(first.Headers.ETag?.ToString(), second.Headers.ETag?.ToString());
        Assert.False(string.IsNullOrEmpty(second.Headers.ETag?.ToString()));
    }

    /// <summary>
    /// The DTO's own contract says it does not carry raw error detail. This is what makes that true
    /// rather than aspirational: the field does not exist on the wire, so there is nothing for a
    /// future edit to start populating without the type system noticing.
    /// </summary>
    [Fact]
    public async Task The_execution_view_does_not_expose_raw_error_detail()
    {
        var number = await SendAsync(recipient: string.Empty);
        await factory.RunOutboxAsync();

        var delivery = await DeliveryForAsync(number);
        var execution = await factory.Services.CreateScope().ServiceProvider
            .GetRequiredService<AppDbContext>().ActionExecutions.AsNoTracking()
            .SingleOrDefaultAsync(e => e.DeliveryId == delivery.Id);

        // This path (a policy-allowed send with no proposal) may not always produce an execution;
        // the field-level contract is the point either way, proven directly against the wire shape.
        using var client = await factory.ClientForAsync("carlos@demo");
        var executionId = execution?.Id ?? await CreateFailedExecutionAsync(client);

        using var response = await client.GetAsync($"/api/action-executions/{executionId}");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.TryGetProperty("errorDetail", out _));
        Assert.True(document.RootElement.TryGetProperty("errorCode", out var code));
        Assert.False(string.IsNullOrEmpty(code.GetString()));
    }

    // --- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> CreateFailedExecutionAsync(HttpClient client)
    {
        var number = await CreateAndSendAsync(unitPrice: 5_000m);
        var actionId = await ProposeAsync("mark_invoice_paid", number);

        using var response = await client.PostAsync($"/api/actions/{actionId}/approve", content: null);
        response.EnsureSuccessStatusCode();

        return (await ExecutionAsync(actionId)).Id;
    }

    private async Task<(ActionExecutor Executor, ActionExecution Execution)> ReconcilerForAsync(
        IServiceScope scope, Guid executionId)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<ActionExecutor>();
        var execution = await db.ActionExecutions.SingleAsync(e => e.Id == executionId);

        return (executor, execution);
    }

    private async Task<string> CreateDraftAsync(string? recipient = null)
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var customer = await CustomerAsync(recipient);

        using var created = await client.PostWriteAsync("/api/invoices", new
        {
            customerName = customer,
            lines = new[] { new { description = "Concurrency test", quantity = 1, unitPrice = 40m } },
        });
        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<InvoiceDetail>())!.Number;
    }

    private async Task<string> CreateAndSendAsync(decimal unitPrice = 500m)
    {
        using var client = await factory.ClientForAsync("carlos@demo");

        var created = await client.PostWriteAsync("/api/invoices", new
        {
            customerName = "Delta Logística",
            lines = new[] { new { description = "Concurrency test", quantity = 1, unitPrice } },
        });
        created.EnsureSuccessStatusCode();

        var invoice = await created.Content.ReadFromJsonAsync<InvoiceDetail>();
        (await client.PostWriteAsync($"/api/invoices/{invoice!.Number}/send")).EnsureSuccessStatusCode();

        return invoice.Number;
    }

    private Task<string> SendAsync(string? recipient = null) => CreateDraftAndSendAsync(recipient);

    private async Task<string> CreateDraftAndSendAsync(string? recipient)
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateDraftAsync(recipient);

        (await client.PostWriteAsync($"/api/invoices/{number}/send")).EnsureSuccessStatusCode();
        return number;
    }

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

    private async Task<Guid> ExecutionIdForAsync(Guid actionId) => (await ExecutionAsync(actionId)).Id;

    /// <summary>
    /// Lapses one execution's attempt lease. Reaches past the entity on purpose: a lease expires
    /// through the passage of time, and the frozen test clock has none.
    /// </summary>
    private async Task ExpireAttemptLeaseAsync(Guid executionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE action_executions SET "AttemptExpiresAt" = {factory.Clock.UtcNow.AddMinutes(-1)}
            WHERE "Id" = {executionId}
            """);
    }

    /// <summary>
    /// Claims the execution's own idempotency key without completing it — the row a twin holds
    /// mid-request, which is what the filter finds when this same key is used again.
    /// </summary>
    /// <remarks>
    /// <paramref name="operation"/> has to match what the real retry will compute
    /// (<c>"{method} {path}"</c>) exactly: with no stored <c>RequestHash</c>,
    /// <c>TransactionalIdempotencyFilter.SameRequest</c> falls back to comparing it, and a mismatch
    /// there answers <c>422 idempotency_key_payload_mismatch</c> instead of the in-progress case
    /// this is standing in for.
    /// </remarks>
    private async Task ClaimKeyWithoutCompletingAsync(Guid executionId, string email, string operation)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO idempotency_keys
                ("Id", "Key", "UserId", "Operation", "RequestHash", "StatusCode", "ResponseJson", "CreatedAt", "ExpiresAt")
            VALUES
                ({Guid.CreateVersion7()}, {executionId.ToString()}, {userId}, {operation}, NULL, 0, NULL,
                 {factory.Clock.UtcNow}, {factory.Clock.UtcNow + IdempotencyRecord.Retention})
            """);
    }

    private async Task ParkExecutionAsUnknownAsync(Guid executionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE action_executions
            SET "Status" = 'Unknown', "ErrorCode" = 'transport_lost', "NextAttemptAt" = {factory.Clock.UtcNow.AddSeconds(-1)},
                "AttemptExpiresAt" = NULL
            WHERE "Id" = {executionId}
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

    private async Task<InvoiceDelivery> DeliveryForAsync(string number)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.InvoiceDeliveries.AsNoTracking().SingleAsync(d => d.InvoiceNumber == number);
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
}
