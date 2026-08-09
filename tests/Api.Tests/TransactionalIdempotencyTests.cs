using System.Net;
using System.Net.Http.Json;
using Api.Domain;
using Api.Features.Invoices;
using Api.Infrastructure;
using Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Api.Tests;

/// <summary>
/// The idempotency contract, including the failures it exists for.
/// </summary>
/// <remarks>
/// The tests that matter here are the crash ones. A filter that replays a stored answer on a quiet
/// day is easy; the question is what the database looks like when the process stops between the
/// business change and the receipt, and whether the retry after that does the work twice.
/// </remarks>
public class TransactionalIdempotencyTests(FaultyApiFactory factory) : IClassFixture<FaultyApiFactory>
{
    [Fact]
    public async Task A_write_without_a_key_is_refused_before_the_handler_runs()
    {
        using var client = await factory.ClientForAsync("carlos@demo");

        var response = await client.PostAsJsonAsync("/api/invoices", NewInvoice(10m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("idempotency_key_required", problem!.Code);

        Assert.Equal(0, await InvoicesDescribedAsync("No key"));
    }

    [Fact]
    public async Task The_same_key_and_the_same_body_replays_the_stored_answer()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var key = Guid.CreateVersion7().ToString();

        using var first = await client.PostWriteAsync("/api/invoices", NewInvoice(33m, "Replay"), key);
        using var second = await client.PostWriteAsync("/api/invoices", NewInvoice(33m, "Replay"), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var created = await first.Content.ReadFromJsonAsync<InvoiceDetail>();
        var replayed = await second.Content.ReadFromJsonAsync<InvoiceDetail>();

        Assert.Equal(created!.Number, replayed!.Number);
        Assert.Equal(created.Total, replayed.Total);
        Assert.Equal(1, await InvoicesDescribedAsync("Replay"));
    }

    /// <summary>
    /// The same key with different content is a caller bug. Answering it with the first request's
    /// response would hide that bug behind something that looks exactly like success.
    /// </summary>
    [Fact]
    public async Task The_same_key_with_a_different_body_is_refused_and_changes_nothing()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var key = Guid.CreateVersion7().ToString();

        using var first = await client.PostWriteAsync("/api/invoices", NewInvoice(40m, "Mismatch"), key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await client.PostWriteAsync("/api/invoices", NewInvoice(9_000m, "Mismatch"), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("idempotency_key_payload_mismatch", problem!.Code);

        // The original stands, and the second body left no trace.
        Assert.Equal(1, await InvoicesDescribedAsync("Mismatch"));
        var stored = await ReceiptAsync(key);
        Assert.Equal(201, stored.StatusCode);
    }

    /// <summary>
    /// The crash that motivated this whole slice: the handler commits, and the process dies before
    /// the caller hears about it. The effect and the receipt went in together, so the retry replays
    /// rather than repeating.
    /// </summary>
    [Fact]
    public async Task A_commit_whose_response_is_lost_replays_instead_of_writing_twice()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var key = Guid.CreateVersion7().ToString();

        factory.Faults.ThrowOnce(FaultCheckpoint.AfterBusinessTransactionCommitBeforeResponse);

        using var lost = await client.PostWriteAsync("/api/invoices", NewInvoice(21m, "Lost response"), key);
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);

        // It did commit — that is what makes this the dangerous case rather than a simple failure.
        Assert.Equal(1, await InvoicesDescribedAsync("Lost response"));

        using var retry = await client.PostWriteAsync("/api/invoices", NewInvoice(21m, "Lost response"), key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);

        Assert.Equal(1, await InvoicesDescribedAsync("Lost response"));
    }

    /// <summary>
    /// The other side of the same boundary: a crash before the commit takes the business change and
    /// the claim with it, so the key is free and the retry is a first attempt.
    /// </summary>
    [Fact]
    public async Task A_crash_before_the_commit_rolls_back_the_change_and_the_receipt_together()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var key = Guid.CreateVersion7().ToString();

        factory.Faults.ThrowOnce(FaultCheckpoint.BeforeBusinessTransactionCommit);

        using var crashed = await client.PostWriteAsync("/api/invoices", NewInvoice(17m, "Rolled back"), key);
        Assert.Equal(HttpStatusCode.InternalServerError, crashed.StatusCode);

        Assert.Equal(0, await InvoicesDescribedAsync("Rolled back"));
        Assert.Null(await FindReceiptAsync(key));

        using var retry = await client.PostWriteAsync("/api/invoices", NewInvoice(17m, "Rolled back"), key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await InvoicesDescribedAsync("Rolled back"));
    }

    /// <summary>
    /// Two identical requests at the same instant. One commits, the other waits for it and replays;
    /// neither is allowed to run the handler a second time.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_identical_requests_produce_one_effect()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var key = Guid.CreateVersion7().ToString();
        var body = NewInvoice(12m, "Concurrent");

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => client.PostWriteAsync("/api/invoices", body, key)));

        Assert.Equal(1, await InvoicesDescribedAsync("Concurrent"));

        // Whoever did not win either replayed the same answer or was told to come back. Nobody was
        // told the request failed, and nobody got a second invoice.
        foreach (var response in responses)
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"unexpected {(int)response.StatusCode}");

            if (response.StatusCode is HttpStatusCode.Conflict)
            {
                var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
                Assert.Equal("request_in_progress", problem!.Code);
            }

            response.Dispose();
        }
    }

    /// <summary>
    /// A refusal the handler <em>returns</em> is a settled result and is stored, so the retry gets
    /// the same answer without running anything.
    /// </summary>
    [Fact]
    public async Task A_returned_refusal_is_stored_and_replayed()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateAndSendAsync(unitPrice: 5_000m);
        var key = Guid.CreateVersion7().ToString();

        using var first = await client.PostWriteAsync($"/api/invoices/{number}/mark-paid", key: key);
        using var second = await client.PostWriteAsync($"/api/invoices/{number}/mark-paid", key: key);

        // Over the Accountant's settlement ceiling: 403 both times, and the second is the stored one.
        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);

        var receipt = await ReceiptAsync(key);
        Assert.Equal(403, receipt.StatusCode);
        Assert.True(receipt.IsCompleted);
    }

    /// <summary>
    /// A refusal the handler <em>throws</em> — a domain rule violation — rolls the transaction back
    /// and stores nothing.
    /// </summary>
    /// <remarks>
    /// The distinction is not cosmetic. The filter can only store a settled result it was handed;
    /// an exception unwinds past it to the problem-details handler, and caching a response the
    /// filter never saw would mean inventing one. Leaving the key free is safe here precisely
    /// because nothing changed: a retry re-runs and is refused identically.
    /// </remarks>
    [Fact]
    public async Task A_thrown_domain_error_leaves_no_receipt_and_the_key_stays_usable()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateDraftAsync();
        var key = Guid.CreateVersion7().ToString();

        using var first = await client.PostWriteAsync($"/api/invoices/{number}/mark-paid", key: key);
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        Assert.Null(await FindReceiptAsync(key));

        using var second = await client.PostWriteAsync($"/api/invoices/{number}/mark-paid", key: key);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(InvoiceStatus.Draft, await StatusOfAsync(number));
    }

    /// <summary>
    /// Expiry is a deletion, not a filter. The old design ignored old rows on read while the unique
    /// index kept reserving them, so a key could be too old to replay and still taken.
    /// </summary>
    [Fact]
    public async Task Cleanup_removes_expired_receipts_and_leaves_the_rest_replayable()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var stale = Guid.CreateVersion7().ToString();
        var fresh = Guid.CreateVersion7().ToString();

        (await client.PostWriteAsync("/api/invoices", NewInvoice(5m, "Stale receipt"), stale)).Dispose();
        (await client.PostWriteAsync("/api/invoices", NewInvoice(5m, "Fresh receipt"), fresh)).Dispose();

        await ExpireReceiptAsync(stale);

        using var scope = factory.Services.CreateScope();
        var cleanup = scope.ServiceProvider.GetServices<IHostedService>().OfType<IdempotencyCleanupService>().Single();
        var removed = await cleanup.PurgeAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Null(await FindReceiptAsync(stale));
        Assert.NotNull(await FindReceiptAsync(fresh));

        // And the freed key behaves like a new one rather than colliding with a ghost.
        using var reused = await client.PostWriteAsync("/api/invoices", NewInvoice(6m, "Reused key"), stale);
        Assert.Equal(HttpStatusCode.Created, reused.StatusCode);
    }

    /// <summary>
    /// Two bodies too large to fingerprint, sharing everything up to the cap. Hashing a prefix would
    /// make them the same request, and the second would be handed the first one's answer.
    /// </summary>
    /// <remarks>
    /// The refusal is the point, not the status code: an oversized body under an idempotency key has
    /// no safe reading, because the fingerprint is what distinguishes a retry from a different
    /// request. The filter used to truncate silently at 256 KiB and carry on.
    /// </remarks>
    [Fact]
    public async Task An_oversized_body_is_refused_rather_than_fingerprinted_by_its_prefix()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var key = Guid.CreateVersion7().ToString();

        // Identical for the first 256 KiB, different after it. Under the old truncating read these
        // hashed the same, and the second call replayed the first's response.
        var shared = new string('x', TransactionalIdempotencyFilter.MaxBodyBytes);

        using var first = await client.PostWriteAsync(
            "/api/invoices", NewInvoice(10m, shared + "-first"), key);
        using var second = await client.PostWriteAsync(
            "/api/invoices", NewInvoice(9_000m, shared + "-second"), key);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, first.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, second.StatusCode);

        var problem = await second.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("request_body_too_large", problem!.Code);

        // Neither ran, and neither took the key: the refusal happens before the transaction opens.
        Assert.Null(await FindReceiptAsync(key));
    }

    /// <summary>
    /// A handler that cannot get the row it needs. The <c>lock_timeout</c> the filter sets covers the
    /// handler's statements too, so the refusal surfaces from inside the handler rather than at the
    /// claim — and it means the same thing there: somebody else is mid-write, come back.
    /// </summary>
    /// <remarks>
    /// Answering 500 would tell a client its request failed, when what happened is that it has not
    /// been attempted yet. The distinction decides whether retrying is correct.
    /// </remarks>
    [Fact]
    public async Task A_handler_that_cannot_get_its_lock_answers_conflict_rather_than_server_error()
    {
        using var client = await factory.ClientForAsync("carlos@demo");
        var number = await CreateAndSendAsync(unitPrice: 100m);

        // A second connection holding the invoice row, so the handler's UPDATE has to wait for it.
        // The transaction is never committed: it exists only to be in the way.
        await using var blocker = new NpgsqlConnection(factory.ConnectionString);
        await blocker.OpenAsync();
        await using var holding = await blocker.BeginTransactionAsync();

        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = """SELECT 1 FROM invoices WHERE "Number" = @number FOR UPDATE""";
            command.Parameters.AddWithValue("number", number);
            await command.ExecuteNonQueryAsync();
        }

        var key = Guid.CreateVersion7().ToString();
        using var blocked = await client.PostWriteAsync($"/api/invoices/{number}/mark-paid", key: key);

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var problem = await blocked.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("request_in_progress", problem!.Code);

        // The key was this caller's alone, so the timeout can only have come from the handler —
        // which is the case the filter used to answer 500 for.
        Assert.Contains(
            factory.Logs.Records,
            record => record.Contains($"A handler under key {key} timed out", StringComparison.Ordinal));

        await holding.RollbackAsync();

        // Nothing was half-done: the invoice is as it was, and the key the caller used is free.
        Assert.Equal(InvoiceStatus.Sent, await StatusOfAsync(number));
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static object NewInvoice(decimal unitPrice, string description = "No key") => new
    {
        customerName = "Delta Logística",
        lines = new[] { new { description, quantity = 1, unitPrice } },
    };

    private async Task<string> CreateDraftAsync()
    {
        using var client = await factory.ClientForAsync("carlos@demo");

        using var created = await client.PostWriteAsync("/api/invoices", NewInvoice(15m, "Draft fixture"));
        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<InvoiceDetail>())!.Number;
    }

    private async Task<string> CreateAndSendAsync(decimal unitPrice)
    {
        using var client = await factory.ClientForAsync("carlos@demo");

        using var created = await client.PostWriteAsync("/api/invoices", NewInvoice(unitPrice, "Sent fixture"));
        created.EnsureSuccessStatusCode();

        var number = (await created.Content.ReadFromJsonAsync<InvoiceDetail>())!.Number;
        (await client.PostWriteAsync($"/api/invoices/{number}/send")).EnsureSuccessStatusCode();

        return number;
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

    private async Task<int> InvoicesDescribedAsync(string description)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Invoices.AsNoTracking()
            .CountAsync(invoice => invoice.Lines.Any(line => line.Description == description));
    }

    private async Task<IdempotencyRecord> ReceiptAsync(string key) =>
        await FindReceiptAsync(key) ?? throw new InvalidOperationException($"No receipt for '{key}'.");

    private async Task<IdempotencyRecord?> FindReceiptAsync(string key)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(record => record.Key == key);
    }

    private async Task ExpireReceiptAsync(string key)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Reaches past the entity on purpose: expiry is the passage of time, and the frozen test
        // clock has none.
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE idempotency_keys SET "ExpiresAt" = {factory.Clock.UtcNow.AddDays(-1)} WHERE "Key" = {key}""");
    }

    private sealed record ProblemPayload(string? Code, string? Detail);
}
